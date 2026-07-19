using Gazetteer.Core.Interfaces;
using Gazetteer.Core.Models;
using Gazetteer.Infrastructure.Data;
using Gazetteer.Infrastructure.Extensions;
using Gazetteer.Seeder.Configuration;
using Gazetteer.Seeder.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: true);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddGazetteerInfrastructure(builder.Configuration);
builder.Services.Configure<SeederOptions>(builder.Configuration.GetSection("Seeder"));
builder.Services.AddHttpClient<PbfDownloader>();
builder.Services.AddTransient<PbfDownloader>();
builder.Services.AddTransient<OsmParser>();
builder.Services.AddTransient<BulkImporter>();
builder.Services.AddTransient<ElasticsearchIndexer>();
builder.Services.AddTransient<HierarchyBuilder>();
builder.Services.AddTransient<PostcodeSynthesizer>();
builder.Services.AddHttpClient<OnspdImporter>();
builder.Services.AddTransient<OnspdImporter>();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var options = builder.Configuration.GetSection("Seeder").Get<SeederOptions>() ?? new SeederOptions();

await RunSeeder(host.Services, options, logger);

async Task RunSeeder(IServiceProvider services, SeederOptions options, ILogger logger)
{
    var steps = options.Steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => s.ToLowerInvariant())
        .ToHashSet();

    var countryCodes = CountryConfig.ParseCountryCodes(options.Countries).ToList();
    logger.LogInformation("Processing countries: {Countries}", string.Join(", ", countryCodes));
    
    // Ensure database is created
    using (var scope = services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();
        await db.Database.MigrateAsync();
        logger.LogInformation("Database migrated successfully");
    }

    // Seed countries
    var importer = services.GetRequiredService<BulkImporter>();
    var countries = countryCodes
        .Where(c => CountryConfig.EuUkCountries.ContainsKey(c))
        .Select(c => new Country
        {
            Code = c,
            Name = CountryConfig.EuUkCountries[c].Name,
            Continent = "Europe"
        });
    await importer.ImportCountriesAsync(countries);

    foreach (var countryCode in countryCodes)
    {
        logger.LogInformation("=== Processing {Country} ===", countryCode);

        // Look up any existing checkpoint before downloading, so a local file that has
        // regressed to an older copy than what we last seeded from gets re-downloaded.
        var seedCheckpointUtc = options.ForceReseed ? null : await importer.GetSeedCheckpointAsync(countryCode);

        string? pbfFilePath = null;

        // Step 1: Download
        if (steps.Contains("download"))
        {
            var downloader = services.GetRequiredService<PbfDownloader>();
            pbfFilePath = await downloader.DownloadAsync(countryCode, options.DataDirectory, seedCheckpointUtc);
        }
        else
        {
            pbfFilePath = Path.Combine(options.DataDirectory, $"{countryCode.ToLowerInvariant()}-latest.osm.pbf");
        }

        var fileTimestampUtc = File.Exists(pbfFilePath) ? File.GetLastWriteTimeUtc(pbfFilePath) : (DateTime?)null;

        var wantsSeeding = steps.Contains("parse") || steps.Contains("load") || steps.Contains("hierarchy");

        if (wantsSeeding && !options.ForceReseed && seedCheckpointUtc is not null && fileTimestampUtc is not null
            && seedCheckpointUtc >= fileTimestampUtc)
        {
            logger.LogInformation(
                "{Country} already seeded from this file version (seeded from file dated {Checkpoint:u}, current file dated {FileDate:u}); skipping parse/load/hierarchy. Use ForceReseed to override.",
                countryCode, seedCheckpointUtc, fileTimestampUtc);
            continue;
        }

        var seeded = false;

        // Step 2 & 3: Parse and Load
        if (steps.Contains("parse") || steps.Contains("load"))
        {
            if (pbfFilePath == null || !File.Exists(pbfFilePath))
            {
                logger.LogWarning("PBF file not found: {Path}. Run with --steps download first.", pbfFilePath);
                continue;
            }

            // Clear existing data for this country to avoid duplicates on re-run
            await importer.ClearLocationsForCountryAsync(countryCode);

            var parser = services.GetRequiredService<OsmParser>();
            var locations = parser.Parse(pbfFilePath, countryCode);
            await importer.ImportLocationsAsync(locations, options.BatchSize);
            seeded = true;
        }

        // Step 4: Build hierarchy
        if (steps.Contains("hierarchy"))
        {
            var hierarchyBuilder = services.GetRequiredService<HierarchyBuilder>();
            await hierarchyBuilder.BuildHierarchyAsync(countryCode);

            // Synthesize postcode districts/areas after hierarchy is built (so parents are available)
            var postcodeSynthesizer = services.GetRequiredService<PostcodeSynthesizer>();
            await postcodeSynthesizer.SynthesizeAsync(countryCode);
            seeded = true;
        }

        // Record the checkpoint only once the country has been fully (re)processed for this
        // file version, so a crash mid-country still results in "not seeded" on next restart.
        if (seeded && fileTimestampUtc is not null)
        {
            await importer.SetSeedCheckpointAsync(countryCode, fileTimestampUtc.Value);
        }
    }

    // Step 5: Import ONSPD postcodes (UK only, after hierarchy so admin regions exist)
    if (steps.Contains("postcodes") && countryCodes.Contains("GB"))
    {
        var onspdImporter = services.GetRequiredService<OnspdImporter>();
        var onspdFile = options.OnspdFile;

        // Auto-download ONSPD if no file path configured
        if (string.IsNullOrEmpty(onspdFile))
        {
            logger.LogInformation("No ONSPD file configured, downloading automatically...");
            onspdFile = await onspdImporter.DownloadAsync(options.OnspdUrl, options.DataDirectory);
        }

        await onspdImporter.ImportAsync(onspdFile, options.BatchSize);
    }

    // Step 6: Index to Elasticsearch (after all countries and postcodes are loaded)
    if (steps.Contains("index"))
    {
        var indexer = services.GetRequiredService<ElasticsearchIndexer>();
        await indexer.IndexAllAsync(options.BatchSize, options.RecreateIndex);
    }

    logger.LogInformation("Seeder complete!");
}
