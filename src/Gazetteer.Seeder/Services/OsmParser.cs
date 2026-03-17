using Gazetteer.Core.Enums;
using Gazetteer.Core.Models;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using OsmSharp;
using OsmSharp.Streams;

namespace Gazetteer.Seeder.Services;

public class OsmParser
{
    private readonly ILogger<OsmParser> _logger;

    private static readonly Dictionary<string, LocationType> PlaceTagMapping = new()
    {
        ["country"] = LocationType.Country,
        ["state"] = LocationType.AdminRegion1,
        ["region"] = LocationType.AdminRegion1,
        ["province"] = LocationType.AdminRegion1,
        ["county"] = LocationType.AdminRegion2,
        ["district"] = LocationType.AdminRegion2,
        ["municipality"] = LocationType.AdminRegion3,
        ["city"] = LocationType.City,
        ["town"] = LocationType.Town,
        ["village"] = LocationType.Village,
        ["hamlet"] = LocationType.Village,
        ["suburb"] = LocationType.Neighborhood,
        ["neighbourhood"] = LocationType.Neighborhood,
        ["quarter"] = LocationType.Neighborhood,
        ["locality"] = LocationType.Locality,
        ["isolated_dwelling"] = LocationType.Locality,
    };

    private static readonly Dictionary<string, LocationType> AdminLevelMapping = new()
    {
        ["2"] = LocationType.Country,
        ["3"] = LocationType.AdminRegion1,
        ["4"] = LocationType.AdminRegion1,
        ["5"] = LocationType.AdminRegion2,
        ["6"] = LocationType.AdminRegion2,
        ["7"] = LocationType.AdminRegion3,
        ["8"] = LocationType.AdminRegion3,
    };

    public OsmParser(ILogger<OsmParser> logger)
    {
        _logger = logger;
    }

    public IEnumerable<Location> Parse(string pbfFilePath, string countryCode)
    {
        _logger.LogInformation("Parsing PBF file: {File} for country: {Country}", pbfFilePath, countryCode);

        using var fileStream = File.OpenRead(pbfFilePath);
        var source = new PBFOsmStreamSource(fileStream);

        long nodeCount = 0;
        long extractedCount = 0;

        foreach (var osmGeo in source)
        {
            nodeCount++;
            if (nodeCount % 1_000_000 == 0)
                _logger.LogInformation("  Processed {Count:N0} OSM elements, extracted {Extracted:N0} locations",
                    nodeCount, extractedCount);

            var location = TryExtractLocation(osmGeo, countryCode);
            if (location != null)
            {
                extractedCount++;
                yield return location;
            }
        }

        _logger.LogInformation("Finished parsing {File}: {Total:N0} elements, {Extracted:N0} locations",
            pbfFilePath, nodeCount, extractedCount);
    }

    private Location? TryExtractLocation(OsmGeo osmGeo, string countryCode)
    {
        if (osmGeo.Tags == null || !osmGeo.Tags.ContainsKey("name"))
            return null;

        var locationType = DetermineLocationType(osmGeo);
        if (locationType == null)
            return null;

        var name = osmGeo.Tags.GetValueOrDefault("name") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        double lat = 0, lon = 0;
        OsmType osmType;

        switch (osmGeo)
        {
            case Node node when node.Latitude.HasValue && node.Longitude.HasValue:
                lat = node.Latitude.Value;
                lon = node.Longitude.Value;
                osmType = OsmType.Node;
                break;
            case Way way:
                osmType = OsmType.Way;
                break;
            case Relation relation:
                osmType = OsmType.Relation;
                break;
            default:
                return null;
        }

        var location = new Location
        {
            OsmId = osmGeo.Id ?? 0,
            OsmType = osmType,
            Name = name,
            LocalName = osmGeo.Tags.GetValueOrDefault("name:local"),
            NameEn = osmGeo.Tags.GetValueOrDefault("name:en"),
            LocationType = locationType.Value,
            CountryCode = countryCode,
            Latitude = lat,
            Longitude = lon,
            Population = ParsePopulation(osmGeo.Tags.GetValueOrDefault("population")),
            PostalCode = osmGeo.Tags.GetValueOrDefault("addr:postcode")
                         ?? osmGeo.Tags.GetValueOrDefault("postal_code"),
        };

        // Build alternate names from all name:* tags
        var alternates = osmGeo.Tags
            .Where(t => t.Key.StartsWith("name:") && t.Key != "name:local" && t.Key != "name:en")
            .Select(t => t.Value)
            .Distinct();
        var altList = alternates.ToList();
        if (altList.Count > 0)
            location.AlternateNames = string.Join(";", altList);

        return location;
    }

    private static LocationType? DetermineLocationType(OsmGeo osmGeo)
    {
        // Check place tag first
        if (osmGeo.Tags.TryGetValue("place", out var placeValue)
            && PlaceTagMapping.TryGetValue(placeValue, out var placeType))
        {
            return placeType;
        }

        // Check boundary=administrative with admin_level
        if (osmGeo.Tags.TryGetValue("boundary", out var boundary))
        {
            if (boundary == "postal_code")
                return LocationType.Postcode;

            if (boundary == "administrative"
                && osmGeo.Tags.TryGetValue("admin_level", out var adminLevel)
                && AdminLevelMapping.TryGetValue(adminLevel, out var adminType))
            {
                return adminType;
            }
        }

        // Check named roads
        if (osmGeo.Tags.ContainsKey("highway") && osmGeo.Tags.ContainsKey("name"))
            return LocationType.Road;

        return null;
    }

    private static long? ParsePopulation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Handle values like "1,234,567" or "~500000"
        var cleaned = new string(value.Where(c => char.IsDigit(c)).ToArray());
        return long.TryParse(cleaned, out var pop) ? pop : null;
    }
}
