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
        [FromQuery(Name = "q")] string query,
        [FromQuery(Name = "country")] string? countryCode = null,
        [FromQuery(Name = "type")] LocationType? locationType = null,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Query parameter 'q' is required");

        if (limit is < 1 or > 100)
            limit = 20;

        var request = new SearchRequest
        {
            Query = query,
            CountryCode = countryCode,
            LocationType = locationType,
            Limit = limit
        };

        var results = await _searchService.SearchAsync(request, ct);
        return Ok(results);
    }
}
