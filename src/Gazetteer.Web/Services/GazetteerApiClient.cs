using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gazetteer.Core.DTOs;
using Gazetteer.Core.Enums;
using Gazetteer.Core.Models;

namespace Gazetteer.Web.Services;

public class GazetteerApiClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public GazetteerApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<SearchResultDto>> SearchAsync(
        string query,
        string? countryCode = null,
        LocationType? locationType = null,
        Guid? locationGroupId = null,
        int limit = 20)
    {
        var url = $"/api/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        if (!string.IsNullOrEmpty(countryCode))
            url += $"&country={countryCode}";
        if (locationGroupId.HasValue)
            url += $"&groupId={locationGroupId.Value}";
        else if (locationType.HasValue)
            url += $"&type={locationType.Value}";

        var result = await _http.GetFromJsonAsync<List<SearchResultDto>>(url, JsonOptions);
        return result ?? [];
    }

    public async Task<LocationDetailDto?> GetLocationAsync(long id)
    {
        return await _http.GetFromJsonAsync<LocationDetailDto>($"/api/locations/{id}", JsonOptions);
    }

    public async Task<GeoJsonResult?> GetGeometryAsync(long id)
    {
        return await _http.GetFromJsonAsync<GeoJsonResult>($"/api/locations/{id}/geometry", JsonOptions);
    }

    public async Task<List<Country>> GetCountriesAsync()
    {
        var result = await _http.GetFromJsonAsync<List<Country>>("/api/countries", JsonOptions);
        return result ?? [];
    }

    public async Task<List<string>> GetLocationTypesAsync()
    {
        var result = await _http.GetFromJsonAsync<List<string>>("/api/countries/location-types", JsonOptions);
        return result ?? [];
    }

    public async Task<List<LocationGroupSummaryDto>> GetLocationGroupsAsync()
    {
        var result = await _http.GetFromJsonAsync<List<LocationGroupSummaryDto>>("/api/location-groups", JsonOptions);
        return result ?? [];
    }

    public async Task<LocationGroupDetailDto?> GetLocationGroupAsync(Guid id)
    {
        return await _http.GetFromJsonAsync<LocationGroupDetailDto>($"/api/location-groups/{id}", JsonOptions);
    }

    public async Task<LocationGroupDetailDto?> CreateLocationGroupAsync(UpsertLocationGroupRequest request)
    {
        var response = await _http.PostAsJsonAsync("/api/location-groups", request, JsonOptions);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body);
        }

        return await response.Content.ReadFromJsonAsync<LocationGroupDetailDto>(JsonOptions);
    }

    public async Task<LocationGroupDetailDto?> UpdateLocationGroupAsync(Guid id, UpsertLocationGroupRequest request)
    {
        var response = await _http.PutAsJsonAsync($"/api/location-groups/{id}", request, JsonOptions);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body);
        }

        return await response.Content.ReadFromJsonAsync<LocationGroupDetailDto>(JsonOptions);
    }

    public async Task DeleteLocationGroupAsync(Guid id)
    {
        var response = await _http.DeleteAsync($"/api/location-groups/{id}");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body);
        }
    }
}
