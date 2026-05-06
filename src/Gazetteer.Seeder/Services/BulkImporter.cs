using Gazetteer.Core.Interfaces;
using Gazetteer.Core.Models;
using Microsoft.Extensions.Logging;

namespace Gazetteer.Seeder.Services;

public class BulkImporter
{
    private readonly ILocationRepository _repository;
    private readonly ILogger<BulkImporter> _logger;

    public BulkImporter(ILocationRepository repository, ILogger<BulkImporter> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task ImportAsync(List<Location> locations, int batchSize = 5000, CancellationToken ct = default)
    {
        _logger.LogInformation("Importing {Count} locations in batches of {BatchSize}...", locations.Count, batchSize);

        var imported = 0;
        foreach (var batch in locations.Chunk(batchSize))
        {
            await _repository.BulkInsertAsync(batch, ct);
            imported += batch.Length;
            _logger.LogInformation("Imported {Count}/{Total} locations", imported, locations.Count);
        }
    }
}
