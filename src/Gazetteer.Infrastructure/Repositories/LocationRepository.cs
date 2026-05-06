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

    public async Task<Location?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await _db.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    public async Task<Location?> GetByIdWithGeometryAsync(int id, CancellationToken ct = default)
    {
        return await _db.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    public async Task<List<Location>> GetParentChainAsync(int locationId, CancellationToken ct = default)
    {
        var chain = new List<Location>();
        var current = await _db.Locations.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, ct);

        while (current?.ParentId != null)
        {
            current = await _db.Locations.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == current.ParentId, ct);
            if (current != null)
                chain.Add(current);
        }

        chain.Reverse();
        return chain;
    }

    public async Task<List<Country>> GetCountriesAsync(CancellationToken ct = default)
    {
        return await _db.Countries.AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task BulkInsertAsync(IEnumerable<Location> locations, CancellationToken ct = default)
    {
        _db.Locations.AddRange(locations);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<Location>> GetByCountryAsync(string countryCode, CancellationToken ct = default)
    {
        return await _db.Locations
            .AsNoTracking()
            .Where(l => l.CountryCode == countryCode)
            .ToListAsync(ct);
    }

    public async Task<List<Location>> GetLocationsWithGeometryAsync(string countryCode, CancellationToken ct = default)
    {
        return await _db.Locations
            .AsNoTracking()
            .Where(l => l.CountryCode == countryCode && l.Geometry != null)
            .ToListAsync(ct);
    }

    public async Task UpdateParentIdsAsync(Dictionary<int, int> childToParentMap, CancellationToken ct = default)
    {
        if (childToParentMap.Count == 0) return;

        foreach (var batch in childToParentMap.Chunk(500))
        {
            var ids = batch.Select(b => b.Key).ToList();
            var locations = await _db.Locations
                .Where(l => ids.Contains(l.Id))
                .ToListAsync(ct);

            foreach (var loc in locations)
            {
                if (childToParentMap.TryGetValue(loc.Id, out var parentId))
                    loc.ParentId = parentId;
            }

            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task SeedCountriesAsync(IEnumerable<Country> countries, CancellationToken ct = default)
    {
        foreach (var country in countries)
        {
            var existing = await _db.Countries.FindAsync([country.Code], ct);
            if (existing == null)
                _db.Countries.Add(country);
            else
            {
                existing.Name = country.Name;
                existing.Continent = country.Continent;
            }
        }
        await _db.SaveChangesAsync(ct);
    }
}
