using System.Net.Http.Json;
using Gazetteer.Core.DTOs;
using Gazetteer.Core.Enums;
using Gazetteer.Core.Models;

namespace Gazetteer.Web.Services;

public class GazetteerApiClient
{
    private readonly HttpClient _http;

    public GazetteerApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<SearchResultDto>> SearchAsync(
        string query, string? countryCode = null, LocationType? locationType = null, int limit = 20)
    {
        var url = $"/api/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        if (!string.IsNullOrEmpty(countryCode))
            url += $"&country={countryCode}";
        if (locationType.HasValue)
            url += $"&type={locationType.Value}";

        var result = await _http.GetFromJsonAsync<List<SearchResultDto>>(url);
        return result ?? new List<SearchResultDto>();
    }

    public async Task<LocationDetailDto?> GetLocationAsync(long id)
    {
        return await _http.GetFromJsonAsync<LocationDetailDto>($"/api/locations/{id}");
    }

    public async Task<GeoJsonResult?> GetGeometryAsync(long id)
    {
        return await _http.GetFromJsonAsync<GeoJsonResult>($"/api/locations/{id}/geometry");
    }

    public async Task<List<Country>> GetCountriesAsync()
    {
        var result = await _http.GetFromJsonAsync<List<Country>>("/api/countries");
        return result ?? new List<Country>();
    }

    public async Task<List<string>> GetLocationTypesAsync()
    {
        var result = await _http.GetFromJsonAsync<List<string>>("/api/countries/location-types");
        return result ?? new List<string>();
    }
}
