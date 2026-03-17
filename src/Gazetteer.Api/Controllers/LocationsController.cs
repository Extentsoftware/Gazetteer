using Gazetteer.Core.DTOs;
using Gazetteer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Gazetteer.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LocationsController : ControllerBase
{
    private readonly ISearchService _searchService;

    public LocationsController(ISearchService searchService)
    {
        _searchService = searchService;
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<LocationDetailDto>> GetById(long id, CancellationToken ct)
    {
        var result = await _searchService.GetLocationDetailAsync(id, ct);
        if (result == null)
            return NotFound();

        return Ok(result);
    }

    [HttpGet("{id:long}/geometry")]
    public async Task<ActionResult<GeoJsonResult>> GetGeometry(long id, CancellationToken ct)
    {
        var result = await _searchService.GetLocationGeometryAsync(id, ct);
        if (result == null)
            return NotFound();

        return Ok(result);
    }
}
