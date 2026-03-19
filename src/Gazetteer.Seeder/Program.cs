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
    logger.LogInformation("Steps: {Steps}", string.Join(", ", steps));

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

        string? pbfFilePath = null;

        // Step 1: Download
        if (steps.Contains("download"))
        {
            var downloader = services.GetRequiredService<PbfDownloader>();
            pbfFilePath = await downloader.DownloadAsync(countryCode, options.DataDirectory);
        }
        else
        {
            pbfFilePath = Path.Combine(options.DataDirectory, $"{countryCode.ToLowerInvariant()}-latest.osm.pbf");
            if (!File.Exists(pbfFilePath))
            {
                logger.LogWarning("PBF file not found: {Path}. Run with --steps download first.", pbfFilePath);
                continue;
            }
        }

        // Step 2 & 3: Parse and Load
        if (steps.Contains("parse") || steps.Contains("load"))
        {
            // Clear existing data for this country to avoid duplicates on re-run
            await importer.ClearLocationsForCountryAsync(countryCode);

            var parser = services.GetRequiredService<OsmParser>();
            var locations = parser.Parse(pbfFilePath, countryCode);
            await importer.ImportLocationsAsync(locations, options.BatchSize);
        }

        // Step 4: Build hierarchy
        if (steps.Contains("hierarchy"))
        {
            var hierarchyBuilder = services.GetRequiredService<HierarchyBuilder>();
            await hierarchyBuilder.BuildHierarchyAsync(countryCode);
        }
    }

    // Step 5: Index to Elasticsearch (after all countries are loaded)
    if (steps.Contains("index"))
    {
        var indexer = services.GetRequiredService<ElasticsearchIndexer>();
        await indexer.IndexAllAsync(options.BatchSize, options.RecreateIndex);
    }

    logger.LogInformation("Seeder complete!");
}
