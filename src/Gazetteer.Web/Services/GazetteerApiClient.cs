using System.Net.Http.Json;
using Gazetteer.Core.DTOs;
using Gazetteer.Core.Models;

namespace Gazetteer.Web.Services;

public class GazetteerApiClient
{
    private readonly HttpClient _http;

    public GazetteerApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<SearchResultDto>> SearchAsync(string query, string? countryCode = null, string? locationType = null, int limit = 20)
    {
        var url = $"api/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        if (!string.IsNullOrEmpty(countryCode))
            url += $"&country={Uri.EscapeDataString(countryCode)}";
        if (!string.IsNullOrEmpty(locationType))
            url += $"&type={Uri.EscapeDataString(locationType)}";

        return await _http.GetFromJsonAsync<List<SearchResultDto>>(url) ?? [];
    }

    public async Task<LocationDetailDto?> GetLocationAsync(int id)
    {
        return await _http.GetFromJsonAsync<LocationDetailDto>($"api/locations/{id}");
    }

    public async Task<object?> GetGeometryAsync(int id)
    {
        return await _http.GetFromJsonAsync<object>($"api/locations/{id}/geometry");
    }

    public async Task<List<Country>> GetCountriesAsync()
    {
        return await _http.GetFromJsonAsync<List<Country>>("api/countries") ?? [];
    }

    public async Task<List<LocationTypeOption>> GetLocationTypesAsync()
    {
        return await _http.GetFromJsonAsync<List<LocationTypeOption>>("api/countries/location-types") ?? [];
    }
}

public class LocationTypeOption
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
