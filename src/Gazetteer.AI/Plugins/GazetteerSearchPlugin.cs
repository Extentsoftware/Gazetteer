using Gazetteer.AI.Models;
using Gazetteer.Core.DTOs;
using Gazetteer.Core.Enums;
using Gazetteer.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace Gazetteer.AI.Plugins;

public class GazetteerSearchPlugin(
    IServiceScopeFactory scopeFactory,
    ILogger<GazetteerSearchPlugin> logger)
{
    [KernelFunction]
    [Description("Search for locations by name, address, postcode, or landmark description. Use this to find places mentioned by the caller. Returns ranked results with parent hierarchy (e.g., street > neighborhood > borough > city > country).")]
    public async Task<List<LocationCandidate>> SearchLocations(
        [Description("Search query - street name, place name, postcode, landmark, etc.")] string query,
        [Description("Optional ISO country code to filter results (e.g., 'GB' for UK)")] string? countryCode = null,
        [Description("Optional location type filter: Road, City, Town, Village, Neighborhood, Postcode, Amenity")] string? locationType = null,
        [Description("Optional OSM ID of a parent location to search within (e.g., search only within Greater London)")] long? withinOsmId = null)
    {
        logger.LogInformation("Searching: {Query} (country={Country}, type={Type}, within={Within})",
            query, countryCode, locationType, withinOsmId);

        using var scope = scopeFactory.CreateScope();
        var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();

        LocationType? typeFilter = null;
        if (!string.IsNullOrEmpty(locationType) && Enum.TryParse<LocationType>(locationType, true, out var parsed))
            typeFilter = parsed;

        var request = new GazetteerSearchRequest
        {
            Query = query,
            CountryCode = countryCode,
            LocationType = typeFilter,
            WithinOsmId = withinOsmId,
            Limit = 10
        };

        var results = await searchService.SearchAsync(request);

        return results.Select(r => new LocationCandidate
        {
            Id = r.Id,
            Name = r.Name,
            LocationType = r.LocationType.ToString(),
            FullAddress = string.Join(" > ", r.ParentChain?.Select(p => p.Name) ?? []),
            Latitude = r.Latitude,
            Longitude = r.Longitude,
            Confidence = Math.Round(r.Score / 20000.0, 2), // Normalize score to 0-1 range
            MatchReason = $"Matched '{query}' in {r.LocationType}",
            SupportingClues = [query]
        }).ToList();
    }

    [KernelFunction]
    [Description("Get detailed information about a specific location by its ID, including full hierarchy and whether it has polygon geometry.")]
    public async Task<object?> GetLocationDetail(
        [Description("The location ID from a search result")] long locationId)
    {
        logger.LogInformation("Getting detail for location {Id}", locationId);

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILocationRepository>();

        var location = await repository.GetByIdAsync(locationId);
        if (location == null) return null;

        var parents = await repository.GetParentChainAsync(locationId);

        return new
        {
            location.Id,
            location.Name,
            location.LocalName,
            location.NameEn,
            LocationType = location.LocationType.ToString(),
            location.CountryCode,
            location.Latitude,
            location.Longitude,
            location.Population,
            location.PostalCode,
            location.AlternateNames,
            HasGeometry = location.Geometry != null,
            Parents = parents.Select(p => new { p.Name, LocationType = p.LocationType.ToString() })
        };
    }

    [KernelFunction]
    [Description("Look up a UK postcode or partial postcode (e.g., 'BR6', 'BR6 0QL', 'SW1A') to find the location. Also works for partial postcodes.")]
    public async Task<List<LocationCandidate>> LookupPostcode(
        [Description("Full or partial UK postcode (e.g., 'BR6 0QL', 'BR6', 'SW1A 1AA')")] string postcode)
    {
        logger.LogInformation("Looking up postcode: {Postcode}", postcode);
        return await SearchLocations(postcode, "GB", "Postcode");
    }
}
