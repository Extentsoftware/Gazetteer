using Gazetteer.Core.DTOs;
using Gazetteer.Core.Enums;
using Gazetteer.Core.Interfaces;
using Gazetteer.Core.Models;
using Gazetteer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Gazetteer.Infrastructure.Services;

public class LocationGroupService : ILocationGroupService
{
    private readonly GazetteerDbContext _db;

    public LocationGroupService(GazetteerDbContext db)
    {
        _db = db;
    }

    public async Task<List<LocationGroupSummaryDto>> ListAsync(CancellationToken ct = default)
    {
        return await _db.LocationGroups
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new LocationGroupSummaryDto
            {
                Id = g.Id,
                Name = g.Name,
                MemberCount = g.Members.Count
            })
            .ToListAsync(ct);
    }

    public async Task<LocationGroupDetailDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var group = await _db.LocationGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == id, ct);

        return group == null ? null : ToDetail(group);
    }

    public async Task<LocationGroupDetailDto> CreateAsync(UpsertLocationGroupRequest request, CancellationToken ct = default)
    {
        Validate(request);

        var now = DateTime.UtcNow;
        var group = new LocationGroup
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Members = BuildMembers(Guid.Empty, request.Members)
        };

        // Fix GroupId on members after Id is known
        foreach (var member in group.Members)
            member.GroupId = group.Id;

        _db.LocationGroups.Add(group);
        await _db.SaveChangesAsync(ct);
        return ToDetail(group);
    }

    public async Task<LocationGroupDetailDto?> UpdateAsync(Guid id, UpsertLocationGroupRequest request, CancellationToken ct = default)
    {
        Validate(request);

        var group = await _db.LocationGroups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == id, ct);

        if (group == null)
            return null;

        group.Name = request.Name.Trim();
        group.UpdatedAtUtc = DateTime.UtcNow;

        _db.LocationGroupMembers.RemoveRange(group.Members);
        group.Members = BuildMembers(group.Id, request.Members);

        await _db.SaveChangesAsync(ct);
        return ToDetail(group);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var group = await _db.LocationGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group == null)
            return false;

        _db.LocationGroups.Remove(group);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static void Validate(UpsertLocationGroupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Group name is required.");

        if (request.Members.Count == 0)
            throw new ArgumentException("At least one location type is required.");

        if (request.Members.Select(m => m.LocationType).Distinct().Count() != request.Members.Count)
            throw new ArgumentException("Duplicate location types are not allowed.");

        foreach (var member in request.Members)
        {
            if (member.Rank < 1)
                throw new ArgumentException($"Rank for {member.LocationType} must be >= 1.");
            if (member.Boost <= 0)
                throw new ArgumentException($"Boost for {member.LocationType} must be > 0.");
        }
    }

    private static List<LocationGroupMember> BuildMembers(Guid groupId, List<LocationGroupMemberDto> members)
    {
        // Normalize ranks to 1..n in the order provided (by Rank ascending)
        var ordered = members.OrderBy(m => m.Rank).ThenBy(m => m.LocationType).ToList();
        var result = new List<LocationGroupMember>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            result.Add(new LocationGroupMember
            {
                GroupId = groupId,
                LocationType = m.LocationType,
                Rank = i + 1,
                Boost = m.Boost > 0 ? m.Boost : DefaultBoost(i + 1, ordered.Count)
            });
        }

        return result;
    }

    /// <summary>Default boost = (memberCount - rank + 1) * 10.</summary>
    public static float DefaultBoost(int rank, int memberCount) =>
        Math.Max(1, memberCount - rank + 1) * 10f;

    private static LocationGroupDetailDto ToDetail(LocationGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        CreatedAtUtc = group.CreatedAtUtc,
        UpdatedAtUtc = group.UpdatedAtUtc,
        Members = group.Members
            .OrderBy(m => m.Rank)
            .Select(m => new LocationGroupMemberDto
            {
                LocationType = m.LocationType,
                Rank = m.Rank,
                Boost = m.Boost
            })
            .ToList()
    };
}
