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

    private async Task InsertBatchAsync(List<Location> batch, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();

        db.Locations.AddRange(batch);
        await db.SaveChangesAsync(ct);
    }
}
