using Gazetteer.Core.DTOs;
using Gazetteer.Core.Enums;
using Gazetteer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Gazetteer.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly ISearchService _searchService;

    public SearchController(ISearchService searchService)
    {
        _searchService = searchService;
    }

    [HttpGet]
    public async Task<ActionResult<List<SearchResultDto>>> Search(
        [FromQuery] string q,
        [FromQuery] string? country = null,
        [FromQuery] LocationType? type = null,
        [FromQuery] long? within = null,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return BadRequest("Query must be at least 2 characters");

        if (limit is < 1 or > 100)
            limit = 20;

        var request = new GazetteerSearchRequest
        {
            Query = q,
            CountryCode = country,
            LocationType = type,
            WithinOsmId = within,
            Limit = limit
        };

        var results = await _searchService.SearchAsync(request, ct);
        return Ok(results);
    }
}
