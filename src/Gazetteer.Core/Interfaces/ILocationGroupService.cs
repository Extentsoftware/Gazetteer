using Gazetteer.Core.DTOs;

namespace Gazetteer.Core.Interfaces;

public interface ILocationGroupService
{
    Task<List<LocationGroupSummaryDto>> ListAsync(CancellationToken ct = default);
    Task<LocationGroupDetailDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<LocationGroupDetailDto> CreateAsync(UpsertLocationGroupRequest request, CancellationToken ct = default);
    Task<LocationGroupDetailDto?> UpdateAsync(Guid id, UpsertLocationGroupRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
