using Gazetteer.Core.Models;
using Gazetteer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gazetteer.Seeder.Services;

public class BulkImporter
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BulkImporter> _logger;

    public BulkImporter(IServiceProvider serviceProvider, ILogger<BulkImporter> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task ClearLocationsForCountryAsync(string countryCode, CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();

        // Bulk deletes can be slow on large countries — extend timeout
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        // Clear parent references first to avoid FK constraint issues
        await db.Locations
            .Where(l => l.CountryCode == countryCode)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ParentId, (long?)null), ct);

        var deleted = await db.Locations
            .Where(l => l.CountryCode == countryCode)
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation("Cleared {Count:N0} existing locations for {Country}", deleted, countryCode);
    }

    public async Task ImportLocationsAsync(IEnumerable<Location> locations, int batchSize, CancellationToken ct = default)
    {
        var batch = new List<Location>(batchSize);
        long totalImported = 0;

        foreach (var location in locations)
        {
            batch.Add(location);

            if (batch.Count >= batchSize)
            {
                await InsertBatchAsync(batch, ct);
                totalImported += batch.Count;
                _logger.LogInformation("Imported {Total:N0} locations so far", totalImported);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await InsertBatchAsync(batch, ct);
            totalImported += batch.Count;
        }

        _logger.LogInformation("Import complete: {Total:N0} locations imported", totalImported);
    }

    /// <summary>
    /// Returns the LastWriteTimeUtc of the source file that this country was last fully
    /// seeded from, or null if it has never been (fully) seeded.
    /// </summary>
    public async Task<DateTime?> GetSeedCheckpointAsync(string countryCode, CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();

        return await db.Countries
            .Where(c => c.Code == countryCode)
            .Select(c => c.LastSeededFileTimestampUtc)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Records that a country has been fully seeded (parse+load+hierarchy+postcodes)
    /// from a source file with the given LastWriteTimeUtc, so a future run can skip
    /// reprocessing it if the file hasn't changed.
    /// </summary>
    public async Task SetSeedCheckpointAsync(string countryCode, DateTime sourceFileTimestampUtc, CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();

        await db.Countries
            .Where(c => c.Code == countryCode)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LastSeededFileTimestampUtc, sourceFileTimestampUtc)
                .SetProperty(c => c.LastSeededAtUtc, DateTime.UtcNow), ct);

        _logger.LogInformation("Recorded seed checkpoint for {Country} (source file dated {Timestamp:u})",
            countryCode, sourceFileTimestampUtc);
    }

    public async Task ImportCountriesAsync(IEnumerable<Country> countries, CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();

        foreach (var country in countries)
        {
            var exists = await db.Countries.AnyAsync(c => c.Code == country.Code, ct);
            if (!exists)
                db.Countries.Add(country);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Countries imported/updated");
    }

    // Bounded so we don't overwhelm the Npgsql connection pool (default max 100) or the
    // Postgres server; sub-batches have no FK dependencies on each other at this stage
    // (ParentId is only populated later, during hierarchy building), so they're safe to
    // insert concurrently.
    private const int InsertConcurrency = 4;

    private async Task InsertBatchAsync(List<Location> batch, CancellationToken ct)
    {
        // Split into smaller sub-batches to avoid EF Core OOM in CommandBatchPreparer.
        // Locations with geometry + self-referencing FK create large in-memory graphs.
        const int subBatchSize = 500;

        var subBatches = new List<List<Location>>();
        for (int i = 0; i < batch.Count; i += subBatchSize)
        {
            subBatches.Add(batch.GetRange(i, Math.Min(subBatchSize, batch.Count - i)));
        }

        await Parallel.ForEachAsync(
            subBatches,
            new ParallelOptions { MaxDegreeOfParallelism = InsertConcurrency, CancellationToken = ct },
            async (subBatch, token) =>
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();
                db.ChangeTracker.AutoDetectChangesEnabled = false;

                db.Locations.AddRange(subBatch);
                await db.SaveChangesAsync(token);
            });
    }
}
