using Gazetteer.Core.Models;

namespace Gazetteer.Core.Interfaces;

public interface ILocationRepository
{
    Task<Location?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<Location?> GetByIdWithGeometryAsync(long id, CancellationToken ct = default);
    Task<List<Location>> GetParentChainAsync(long locationId, CancellationToken ct = default);
    Task<List<Country>> GetCountriesAsync(CancellationToken ct = default);
    Task BulkInsertAsync(IEnumerable<Location> locations, CancellationToken ct = default);
    Task BulkInsertCountriesAsync(IEnumerable<Country> countries, CancellationToken ct = default);
}
