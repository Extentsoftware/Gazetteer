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

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var detail = await _searchService.GetLocationDetailAsync(id, ct);
        if (detail == null) return NotFound();
        return Ok(detail);
    }

    [HttpGet("{id:int}/geometry")]
    public async Task<IActionResult> GetGeometry(int id, CancellationToken ct)
    {
        var geoJson = await _searchService.GetLocationGeometryAsync(id, ct);
        if (geoJson == null) return NotFound();
        return Ok(geoJson);
    }
}
