using Gazetteer.Core.Interfaces;
using Gazetteer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gazetteer.Seeder.Services;

public class ElasticsearchIndexer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ElasticsearchIndexer> _logger;

    public ElasticsearchIndexer(IServiceProvider serviceProvider, ILogger<ElasticsearchIndexer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task IndexAllAsync(int batchSize, bool recreateIndex, CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var esService = scope.ServiceProvider.GetRequiredService<IElasticsearchService>();
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();

        if (recreateIndex)
        {
            _logger.LogInformation("Deleting existing Elasticsearch index...");
            await esService.DeleteIndexAsync(ct);
        }

        await esService.CreateIndexAsync(ct);

        var totalCount = await db.Locations.CountAsync(ct);
        _logger.LogInformation("Indexing {Count:N0} locations to Elasticsearch...", totalCount);

        int offset = 0;
        long indexed = 0;

        while (offset < totalCount)
        {
            var locations = await db.Locations
                .AsNoTracking()
                .OrderBy(l => l.Id)
                .Skip(offset)
                .Take(batchSize)
                .Select(l => new
                {
                    l.Id,
                    l.Name,
                    l.NameEn,
                    l.AlternateNames,
                    LocationType = l.LocationType.ToString(),
                    l.CountryCode,
                    l.PostalCode,
                    l.Latitude,
                    l.Longitude,
                    l.Population,
                    HasGeometry = l.Geometry != null,
                    ParentName = l.Parent != null ? l.Parent.Name : null,
                    GrandParentName = l.Parent != null && l.Parent.Parent != null ? l.Parent.Parent.Name : null
                })
                .ToListAsync(ct);

            var documents = locations.Select(l => new LocationIndexDocument
            {
                Id = l.Id,
                Name = l.Name,
                NameEn = l.NameEn,
                AlternateNames = l.AlternateNames,
                LocationType = l.LocationType,
                CountryCode = l.CountryCode,
                PostalCode = l.PostalCode,
                Latitude = l.Latitude,
                Longitude = l.Longitude,
                Population = l.Population,
                HasGeometry = l.HasGeometry,
                ParentChain = BuildParentChain(l.ParentName, l.GrandParentName)
            }).ToList();

            await esService.BulkIndexAsync(documents, ct);
            indexed += documents.Count;
            offset += batchSize;

            _logger.LogInformation("Indexed {Indexed:N0} / {Total:N0} locations", indexed, totalCount);
        }

        _logger.LogInformation("Elasticsearch indexing complete: {Count:N0} documents", indexed);
    }

    private static string? BuildParentChain(string? parent, string? grandParent)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(parent)) parts.Add(parent);
        if (!string.IsNullOrEmpty(grandParent)) parts.Add(grandParent);
        return parts.Count > 0 ? string.Join(" > ", parts) : null;
    }
}
