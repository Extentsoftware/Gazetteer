using Gazetteer.Core.Enums;
using Gazetteer.Core.Models;
using Gazetteer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO.Compression;

namespace Gazetteer.Seeder.Services;

/// <summary>
/// Imports UK postcodes from the ONS Postcode Directory (ONSPD) zip file.
/// Creates a Location entity for each live postcode with lat/lon coordinates,
/// then assigns each to the nearest admin region via spatial containment.
/// </summary>
public class OnspdImporter
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OnspdImporter> _logger;

    public OnspdImporter(IServiceProvider serviceProvider, ILogger<OnspdImporter> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task ImportAsync(string zipFilePath, int batchSize, CancellationToken ct = default)
    {
        if (!File.Exists(zipFilePath))
        {
            _logger.LogWarning("ONSPD file not found: {Path}", zipFilePath);
            return;
        }

        _logger.LogInformation("Importing postcodes from ONSPD: {Path}", zipFilePath);

        // Clear existing ONSPD postcodes (those with OsmId = 0, since OSM-sourced ones have real OsmIds)
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

            var deleted = await db.Locations
                .Where(l => l.CountryCode == "GB" && l.LocationType == LocationType.Postcode && l.OsmId == 0)
                .ExecuteDeleteAsync(ct);

            _logger.LogInformation("Cleared {Count:N0} existing ONSPD postcodes", deleted);
        }

        // Load admin regions for parent assignment
        _logger.LogInformation("Loading admin regions for parent assignment...");
        List<AdminRegionInfo> adminRegions;
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();
            adminRegions = await db.Locations
                .AsNoTracking()
                .Where(l => l.CountryCode == "GB" && l.LocationType == LocationType.AdminRegion3)
                .Select(l => new AdminRegionInfo { Id = l.Id, Latitude = l.Latitude, Longitude = l.Longitude })
                .ToListAsync(ct);
        }

        _logger.LogInformation("Loaded {Count:N0} admin regions for assignment", adminRegions.Count);

        // Read postcodes from zip and import in batches
        var batch = new List<Location>(batchSize);
        long totalImported = 0;
        long totalSkipped = 0;

        using var zip = ZipFile.OpenRead(zipFilePath);
        var csvFiles = zip.Entries
            .Where(e => e.FullName.StartsWith("Data/multi_csv/") && e.Name.EndsWith(".csv"))
            .OrderBy(e => e.Name)
            .ToList();

        _logger.LogInformation("Found {Count} CSV files in ONSPD zip", csvFiles.Count);

        foreach (var entry in csvFiles)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);

            // Read header
            var header = await reader.ReadLineAsync(ct);
            if (header == null) continue;

            var columns = header.Split(',');
            int pcdsIdx = Array.IndexOf(columns, "pcds");
            int latIdx = Array.IndexOf(columns, "lat");
            int lonIdx = Array.IndexOf(columns, "long");
            int dotermIdx = Array.IndexOf(columns, "doterm");

            if (pcdsIdx < 0 || latIdx < 0 || lonIdx < 0)
            {
                _logger.LogWarning("Skipping {File}: missing required columns", entry.Name);
                continue;
            }

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                var fields = line.Split(',');
                if (fields.Length <= Math.Max(Math.Max(pcdsIdx, latIdx), lonIdx))
                    continue;

                // Skip terminated postcodes
                if (dotermIdx >= 0 && dotermIdx < fields.Length && !string.IsNullOrWhiteSpace(fields[dotermIdx]))
                {
                    totalSkipped++;
                    continue;
                }

                var postcode = fields[pcdsIdx].Trim().Trim('"');
                if (string.IsNullOrEmpty(postcode)) continue;

                if (!double.TryParse(fields[latIdx].Trim().Trim('"'), NumberStyles.Any, CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(fields[lonIdx].Trim().Trim('"'), NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
                {
                    totalSkipped++;
                    continue;
                }

                // Skip postcodes without coordinates
                if (lat == 0 && lon == 0)
                {
                    totalSkipped++;
                    continue;
                }

                // Find nearest admin region
                long? parentId = FindNearestAdminRegion(lat, lon, adminRegions);

                batch.Add(new Location
                {
                    OsmId = 0, // Marker for ONSPD-sourced postcodes
                    OsmType = OsmType.Node,
                    Name = postcode,
                    LocationType = LocationType.Postcode,
                    CountryCode = "GB",
                    Latitude = lat,
                    Longitude = lon,
                    PostalCode = postcode,
                    ParentId = parentId
                });

                if (batch.Count >= batchSize)
                {
                    await InsertBatchAsync(batch, ct);
                    totalImported += batch.Count;
                    batch.Clear();

                    if (totalImported % 50_000 == 0)
                        _logger.LogInformation("Imported {Count:N0} postcodes so far", totalImported);
                }
            }
        }

        // Final batch
        if (batch.Count > 0)
        {
            await InsertBatchAsync(batch, ct);
            totalImported += batch.Count;
        }

        _logger.LogInformation("ONSPD import complete: {Imported:N0} live postcodes imported, {Skipped:N0} skipped (terminated/invalid)",
            totalImported, totalSkipped);
    }

    private async Task InsertBatchAsync(List<Location> batch, CancellationToken ct)
    {
        const int subBatchSize = 500;

        for (int i = 0; i < batch.Count; i += subBatchSize)
        {
            var subBatch = batch.GetRange(i, Math.Min(subBatchSize, batch.Count - i));

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            db.Locations.AddRange(subBatch);
            await db.SaveChangesAsync(ct);
        }
    }

    private static long? FindNearestAdminRegion(double lat, double lon, List<AdminRegionInfo> regions)
    {
        double bestDist = double.MaxValue;
        long? bestId = null;

        foreach (var region in regions)
        {
            var dlat = lat - region.Latitude;
            var dlon = lon - region.Longitude;
            var distSq = dlat * dlat + dlon * dlon;
            if (distSq < bestDist)
            {
                bestDist = distSq;
                bestId = region.Id;
            }
        }

        return bestId;
    }

    private class AdminRegionInfo
    {
        public long Id { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}
