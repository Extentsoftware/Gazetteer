using Gazetteer.Core.Models;

namespace Gazetteer.Core.Interfaces;

public interface ILocationRepository
{
    Task<Location?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Location?> GetByIdWithGeometryAsync(int id, CancellationToken ct = default);
    Task<List<Location>> GetParentChainAsync(int locationId, CancellationToken ct = default);
    Task<List<Country>> GetCountriesAsync(CancellationToken ct = default);
    Task BulkInsertAsync(IEnumerable<Location> locations, CancellationToken ct = default);
    Task<List<Location>> GetByCountryAsync(string countryCode, CancellationToken ct = default);
    Task<List<Location>> GetLocationsWithGeometryAsync(string countryCode, CancellationToken ct = default);
    Task UpdateParentIdsAsync(Dictionary<int, int> childToParentMap, CancellationToken ct = default);
    Task SeedCountriesAsync(IEnumerable<Country> countries, CancellationToken ct = default);
}
