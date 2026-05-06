using Gazetteer.Core.Interfaces;
using Gazetteer.Core.Models;
using Gazetteer.Infrastructure.Data;
using Gazetteer.Infrastructure.Extensions;
using Gazetteer.Seeder.Configuration;
using Gazetteer.Seeder.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;

var countriesOption = new Option<string[]>(
    "--countries",
    description: "Country codes to seed (e.g., GB DE FR). If empty, seeds all countries.")
{ AllowMultipleArgumentsPerToken = true };

var stepsOption = new Option<string[]>(
    "--steps",
    getDefaultValue: () => ["download", "parse", "load", "hierarchy", "index"],
    description: "Steps to run: download, parse, load, hierarchy, index")
{ AllowMultipleArgumentsPerToken = true };

var dataDirOption = new Option<string>(
    "--data-dir",
    getDefaultValue: () => "data",
    description: "Directory for PBF data files");

var batchSizeOption = new Option<int>(
    "--batch-size",
    getDefaultValue: () => 5000,
    description: "Batch size for database inserts");

var recreateIndexOption = new Option<bool>(
    "--recreate-index",
    getDefaultValue: () => false,
    description: "Drop and recreate Elasticsearch indices");

var rootCommand = new RootCommand("Gazetteer data seeder")
{
    countriesOption, stepsOption, dataDirOption, batchSizeOption, recreateIndexOption
};

rootCommand.SetHandler(async (countries, steps, dataDir, batchSize, recreateIndex) =>
{
    var options = new SeederOptions
    {
        Countries = countries,
        Steps = steps,
        DataDir = dataDir,
        BatchSize = batchSize,
        RecreateIndex = recreateIndex
    };

    await RunSeeder(options);
}, countriesOption, stepsOption, dataDirOption, batchSizeOption, recreateIndexOption);

return await rootCommand.InvokeAsync(args);

static async Task RunSeeder(SeederOptions options)
{
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    var services = new ServiceCollection();
    services.AddLogging(b => b.AddConsole());
    services.AddGazetteerInfrastructure(config);
    services.AddSingleton<PbfDownloader>();
    services.AddSingleton<OsmParser>();
    services.AddScoped<BulkImporter>();
    services.AddScoped<HierarchyBuilder>();
    services.AddScoped<ElasticsearchIndexer>();

    var sp = services.BuildServiceProvider();
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var stepsSet = new HashSet<string>(options.Steps, StringComparer.OrdinalIgnoreCase);

    // Migrate database
    using (var scope = sp.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();
        logger.LogInformation("Applying database migrations...");
        await db.Database.MigrateAsync();
    }

    // Seed countries
    using (var scope = sp.CreateScope())
    {
        var repo = scope.ServiceProvider.GetRequiredService<ILocationRepository>();
        var allCountries = CountryConfig.Countries.Values
            .Select(c => new Country { Code = c.Code, Name = c.Name })
            .ToList();
        await repo.SeedCountriesAsync(allCountries);
        logger.LogInformation("Seeded {Count} countries", allCountries.Count);
    }

    // Manage Elasticsearch indices
    if (stepsSet.Contains("index"))
    {
        using var scope = sp.CreateScope();
        var es = scope.ServiceProvider.GetRequiredService<IElasticsearchService>();

        if (options.RecreateIndex)
        {
            logger.LogInformation("Recreating Elasticsearch indices...");
            await es.DeleteIndexAsync();
            await es.DeleteBoundariesIndexAsync();
        }

        await es.CreateIndexAsync();
        await es.CreateBoundariesIndexAsync();
    }

    // Determine which countries to process
    var countriesToProcess = options.Countries.Length > 0
        ? options.Countries
            .Where(c => CountryConfig.Countries.ContainsKey(c))
            .Select(c => CountryConfig.Countries[c])
            .ToList()
        : CountryConfig.Countries.Values.ToList();

    logger.LogInformation("Processing {Count} countries...", countriesToProcess.Count);

    foreach (var country in countriesToProcess)
    {
        logger.LogInformation("=== Processing {Country} ({Code}) ===", country.Name, country.Code);

        string? pbfPath = null;
        List<Location>? locations = null;

        if (stepsSet.Contains("download"))
        {
            var downloader = sp.GetRequiredService<PbfDownloader>();
            pbfPath = await downloader.DownloadAsync(country.GeofabrikPath, options.DataDir);
        }

        if (stepsSet.Contains("parse") && pbfPath != null)
        {
            var parser = sp.GetRequiredService<OsmParser>();
            locations = parser.Parse(pbfPath, country.Code);
        }

        if (stepsSet.Contains("load") && locations != null)
        {
            using var scope = sp.CreateScope();
            var importer = scope.ServiceProvider.GetRequiredService<BulkImporter>();
            await importer.ImportAsync(locations, options.BatchSize);
        }

        if (stepsSet.Contains("hierarchy"))
        {
            using var scope = sp.CreateScope();
            var builder = scope.ServiceProvider.GetRequiredService<HierarchyBuilder>();
            await builder.BuildHierarchyAsync(country.Code);
        }

        if (stepsSet.Contains("index"))
        {
            using var scope = sp.CreateScope();
            var indexer = scope.ServiceProvider.GetRequiredService<ElasticsearchIndexer>();
            await indexer.IndexLocationsAsync(country.Code);
            await indexer.IndexBoundariesAsync(country.Code);
        }

        logger.LogInformation("=== Completed {Country} ===", country.Name);
    }

    logger.LogInformation("Seeding complete!");
}
