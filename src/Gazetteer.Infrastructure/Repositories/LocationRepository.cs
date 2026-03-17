using Gazetteer.Core.Interfaces;
using Gazetteer.Core.Models;
using Gazetteer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Gazetteer.Infrastructure.Repositories;

public class LocationRepository : ILocationRepository
{
    private readonly GazetteerDbContext _db;

    public LocationRepository(GazetteerDbContext db)
    {
        _db = db;
    }

    public async Task<Location?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        return await _db.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    public async Task<Location?> GetByIdWithGeometryAsync(long id, CancellationToken ct = default)
    {
        return await _db.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    public async Task<List<Location>> GetParentChainAsync(long locationId, CancellationToken ct = default)
    {
        var chain = new List<Location>();
        var current = await _db.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, ct);

        while (current?.ParentId != null)
        {
            current = await _db.Locations
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == current.ParentId, ct);

            if (current != null)
                chain.Add(current);
        }

        return chain;
    }

    public async Task<List<Country>> GetCountriesAsync(CancellationToken ct = default)
    {
        return await _db.Countries
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task BulkInsertAsync(IEnumerable<Location> locations, CancellationToken ct = default)
    {
        _db.Locations.AddRange(locations);
        await _db.SaveChangesAsync(ct);
    }

    public async Task BulkInsertCountriesAsync(IEnumerable<Country> countries, CancellationToken ct = default)
    {
        _db.Countries.AddRange(countries);
        await _db.SaveChangesAsync(ct);
    }
}
