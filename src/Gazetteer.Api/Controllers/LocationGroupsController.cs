using Gazetteer.Core.DTOs;
using Gazetteer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gazetteer.Api.Controllers;

[ApiController]
[Route("api/location-groups")]
public class LocationGroupsController : ControllerBase
{
    private readonly ILocationGroupService _groups;

    public LocationGroupsController(ILocationGroupService groups)
    {
        _groups = groups;
    }

    [HttpGet]
    public async Task<ActionResult<List<LocationGroupSummaryDto>>> List(CancellationToken ct)
    {
        return Ok(await _groups.ListAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LocationGroupDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var group = await _groups.GetAsync(id, ct);
        return group == null ? NotFound() : Ok(group);
    }

    [HttpPost]
    public async Task<ActionResult<LocationGroupDetailDto>> Create(
        [FromBody] UpsertLocationGroupRequest request, CancellationToken ct)
    {
        try
        {
            var created = await _groups.CreateAsync(request, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (DbUpdateException)
        {
            return Conflict("A location group with that name already exists.");
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<LocationGroupDetailDto>> Update(
        Guid id, [FromBody] UpsertLocationGroupRequest request, CancellationToken ct)
    {
        try
        {
            var updated = await _groups.UpdateAsync(id, request, ct);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (DbUpdateException)
        {
            return Conflict("A location group with that name already exists.");
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await _groups.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }
}
