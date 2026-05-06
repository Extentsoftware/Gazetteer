using Gazetteer.Core.Enums;
using Gazetteer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Gazetteer.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CountriesController : ControllerBase
{
    private readonly ILocationRepository _repository;

    public CountriesController(ILocationRepository repository)
    {
        _repository = repository;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var countries = await _repository.GetCountriesAsync(ct);
        return Ok(countries);
    }

    [HttpGet("location-types")]
    public IActionResult GetLocationTypes()
    {
        var types = Enum.GetValues<LocationType>()
            .Select(t => new { Value = t.ToString(), Label = t.ToString() });
        return Ok(types);
    }
}
