using Gazetteer.Core.Enums;
using Gazetteer.Core.Interfaces;
using Gazetteer.Core.Models;
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
    public async Task<ActionResult<List<Country>>> GetAll(CancellationToken ct)
    {
        var countries = await _repository.GetCountriesAsync(ct);
        return Ok(countries);
    }

    [HttpGet("location-types")]
    public ActionResult<List<string>> GetLocationTypes()
    {
        var types = Enum.GetNames<LocationType>().ToList();
        return Ok(types);
    }
}
