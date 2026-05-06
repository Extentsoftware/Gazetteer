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
    private static readonly GeometryFactory GeoFactory = new(new PrecisionModel(), 4326);

    public OsmParser(ILogger<OsmParser> logger)
    {
        _logger = logger;
    }

    public List<Location> Parse(string pbfPath, string countryCode)
    {
        _logger.LogInformation("Parsing {Path} for country {Country}...", pbfPath, countryCode);

        var boundaryData = ScanBoundaryRelations(pbfPath);
        _logger.LogInformation("Pass 1 complete: {Count} boundary relations found", boundaryData.RelationMembers.Count);

        var geometryCache = ResolveGeometries(pbfPath, boundaryData);
        _logger.LogInformation("Pass 2 complete: {Ways} ways, {Nodes} nodes cached",
            geometryCache.WayGeometries.Count, boundaryData.RequiredNodeIds.Count);

        var locations = ParseLocations(pbfPath, countryCode, geometryCache);
        _logger.LogInformation("Pass 3 complete: {Count} locations parsed", locations.Count);

        return locations;
    }

    private BoundaryData ScanBoundaryRelations(string pbfPath)
    {
        var data = new BoundaryData();

        using var stream = File.OpenRead(pbfPath);
        var source = new PBFOsmStreamSource(stream);

        foreach (var element in source)
        {
            if (element is not OsmSharp.Relation relation) continue;
            if (relation.Tags == null) continue;

            var isBoundary = relation.Tags.Contains("boundary", "administrative");
            if (!isBoundary) continue;

            var memberInfo = new RelationMemberInfo();

            if (relation.Members != null)
            {
                foreach (var member in relation.Members)
                {
                    if (member.Type == OsmGeoType.Way)
                    {
                        memberInfo.MemberWayIds.Add(member.Id);
                        memberInfo.MemberRoles[member.Id] = member.Role ?? "outer";
                        data.RequiredWayIds.Add(member.Id);
                    }
                    else if (member.Type == OsmGeoType.Node && member.Role == "admin_centre")
                    {
                        memberInfo.AdminCentreNodeId = member.Id;
                        data.RequiredNodeIds.Add(member.Id);
                    }
                }
            }

            data.RelationMembers[relation.Id!.Value] = memberInfo;
        }

        return data;
    }

    private GeometryCache ResolveGeometries(string pbfPath, BoundaryData boundaryData)
    {
        var cache = new GeometryCache();
        var wayNodeIds = new Dictionary<long, List<long>>();

        // Pass 2a: read ways to get their node ID lists
        _logger.LogInformation("Pass 2a: reading {Count} boundary ways...", boundaryData.RequiredWayIds.Count);
        using (var stream = File.OpenRead(pbfPath))
        {
            var source = new PBFOsmStreamSource(stream);
            foreach (var element in source)
            {
                if (element is not OsmSharp.Way way) continue;
                if (way.Id == null || way.Nodes == null) continue;
                if (!boundaryData.RequiredWayIds.Contains(way.Id.Value)) continue;

                wayNodeIds[way.Id.Value] = [..way.Nodes];
                foreach (var nodeId in way.Nodes)
                    boundaryData.RequiredNodeIds.Add(nodeId);
            }
        }

        // Pass 2b: read only required nodes to get coordinates
        _logger.LogInformation("Pass 2b: reading {Count} required nodes...", boundaryData.RequiredNodeIds.Count);
        var nodeCoords = new Dictionary<long, Coordinate>();
        using (var stream = File.OpenRead(pbfPath))
        {
            var source = new PBFOsmStreamSource(stream);
            var remaining = boundaryData.RequiredNodeIds.Count;

            foreach (var element in source)
            {
                if (remaining == 0) break;
                if (element is not OsmSharp.Node node) continue;
                if (node.Id == null || node.Latitude == null || node.Longitude == null) continue;
                if (!boundaryData.RequiredNodeIds.Contains(node.Id.Value)) continue;

                nodeCoords[node.Id.Value] = new Coordinate(node.Longitude.Value, node.Latitude.Value);
                remaining--;
            }
        }

        // Build way geometries from resolved nodes
        foreach (var (wayId, nodeIds) in wayNodeIds)
        {
            var coords = new List<Coordinate>();
            foreach (var nodeId in nodeIds)
            {
                if (nodeCoords.TryGetValue(nodeId, out var coord))
                    coords.Add(coord);
            }
            if (coords.Count >= 2)
                cache.WayGeometries[wayId] = coords.ToArray();
        }

        // Build relation geometries
        foreach (var (relationId, memberInfo) in boundaryData.RelationMembers)
        {
            var geometry = BuildRelationGeometry(memberInfo, cache);
            if (geometry != null)
                cache.RelationGeometries[relationId] = geometry;

            if (memberInfo.AdminCentreNodeId.HasValue &&
                nodeCoords.TryGetValue(memberInfo.AdminCentreNodeId.Value, out var centreCoord))
            {
                cache.AdminCentreCoords[relationId] = centreCoord;
            }
        }

        return cache;
    }

    private Geometry? BuildRelationGeometry(RelationMemberInfo memberInfo, GeometryCache cache)
    {
        var outerSegments = new List<Coordinate[]>();
        var innerSegments = new List<Coordinate[]>();

        foreach (var wayId in memberInfo.MemberWayIds)
        {
            if (!cache.WayGeometries.TryGetValue(wayId, out var coords)) continue;
            var role = memberInfo.MemberRoles.GetValueOrDefault(wayId, "outer");

            if (role == "inner")
                innerSegments.Add(coords);
            else
                outerSegments.Add(coords);
        }

        var outerRings = MergeWaySegments(outerSegments);
        var innerRings = MergeWaySegments(innerSegments);

        if (outerRings.Count == 0) return null;

        var polygons = new List<Polygon>();
        foreach (var outerRing in outerRings)
        {
            var shell = GeoFactory.CreateLinearRing(outerRing);
            var holes = innerRings.Select(r => GeoFactory.CreateLinearRing(r)).ToArray();
            polygons.Add(GeoFactory.CreatePolygon(shell, holes));
        }

        return polygons.Count == 1 ? polygons[0] : GeoFactory.CreateMultiPolygon(polygons.ToArray());
    }

    private List<Coordinate[]> MergeWaySegments(List<Coordinate[]> segments)
    {
        var rings = new List<Coordinate[]>();
        if (segments.Count == 0) return rings;

        var remaining = new List<Coordinate[]>(segments);

        while (remaining.Count > 0)
        {
            var current = remaining[0].ToList();
            remaining.RemoveAt(0);

            var merged = true;
            while (merged)
            {
                merged = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var seg = remaining[i];
                    var currentFirst = current[0];
                    var currentLast = current[^1];
                    var segFirst = seg[0];
                    var segLast = seg[^1];

                    if (currentLast.Equals2D(segFirst))
                    {
                        current.AddRange(seg.Skip(1));
                        remaining.RemoveAt(i);
                        merged = true;
                    }
                    else if (currentLast.Equals2D(segLast))
                    {
                        current.AddRange(seg.Reverse().Skip(1));
                        remaining.RemoveAt(i);
                        merged = true;
                    }
                    else if (currentFirst.Equals2D(segLast))
                    {
                        current.InsertRange(0, seg.Take(seg.Length - 1));
                        remaining.RemoveAt(i);
                        merged = true;
                    }
                    else if (currentFirst.Equals2D(segFirst))
                    {
                        current.InsertRange(0, seg.Reverse().Skip(1));
                        remaining.RemoveAt(i);
                        merged = true;
                    }
                }
            }

            // Close the ring if nearly closed
            if (current.Count >= 4)
            {
                if (!current[0].Equals2D(current[^1]))
                {
                    var dist = current[0].Distance(current[^1]);
                    if (dist < 0.0001)
                        current[^1] = current[0];
                    else
                        current.Add(new Coordinate(current[0].X, current[0].Y));
                }
                rings.Add(current.ToArray());
            }
        }

        return rings;
    }

    private List<Location> ParseLocations(string pbfPath, string countryCode, GeometryCache cache)
    {
        var locations = new List<Location>();

        using var stream = File.OpenRead(pbfPath);
        var source = new PBFOsmStreamSource(stream);

        foreach (var element in source)
        {
            if (element.Tags == null || element.Id == null) continue;

            var location = element switch
            {
                OsmSharp.Node node => ParseNode(node, countryCode),
                OsmSharp.Way way => ParseWay(way, countryCode),
                OsmSharp.Relation relation => ParseRelation(relation, countryCode, cache),
                _ => null
            };

            if (location != null)
                locations.Add(location);
        }

        return locations;
    }

    private Location? ParseNode(OsmSharp.Node node, string countryCode)
    {
        if (node.Latitude == null || node.Longitude == null) return null;

        var locationType = DetermineLocationType(node.Tags!);
        if (locationType == null) return null;

        var name = GetName(node.Tags!);
        if (string.IsNullOrEmpty(name)) return null;

        return new Location
        {
            OsmId = node.Id!.Value,
            OsmType = OsmType.Node,
            Name = name,
            LocalName = node.Tags!.GetValue("name:local") ?? node.Tags.GetValue("loc_name"),
            NameEn = node.Tags.GetValue("name:en"),
            AlternateNames = GetAlternateNames(node.Tags),
            LocationType = locationType.Value,
            CountryCode = countryCode,
            Lat = node.Latitude.Value,
            Lon = node.Longitude.Value,
            Population = GetPopulation(node.Tags),
            PostalCode = node.Tags.GetValue("postal_code") ?? node.Tags.GetValue("addr:postcode")
        };
    }

    private Location? ParseWay(OsmSharp.Way way, string countryCode)
    {
        if (way.Tags == null) return null;

        var isHighway = way.Tags.ContainsKey("highway") && way.Tags.ContainsKey("name");
        if (!isHighway) return null;

        var name = GetName(way.Tags);
        if (string.IsNullOrEmpty(name)) return null;

        return new Location
        {
            OsmId = way.Id!.Value,
            OsmType = OsmType.Way,
            Name = name,
            LocalName = way.Tags.GetValue("name:local") ?? way.Tags.GetValue("loc_name"),
            NameEn = way.Tags.GetValue("name:en"),
            AlternateNames = GetAlternateNames(way.Tags),
            LocationType = LocationType.Road,
            CountryCode = countryCode,
            PostalCode = way.Tags.GetValue("postal_code") ?? way.Tags.GetValue("addr:postcode")
        };
    }

    private Location? ParseRelation(OsmSharp.Relation relation, string countryCode, GeometryCache cache)
    {
        if (relation.Tags == null) return null;

        var locationType = DetermineLocationType(relation.Tags);
        if (locationType == null) return null;

        var name = GetName(relation.Tags);
        if (string.IsNullOrEmpty(name)) return null;

        double lat = 0, lon = 0;
        Geometry? geometry = null;

        // Use admin_centre coordinates if available
        if (cache.AdminCentreCoords.TryGetValue(relation.Id!.Value, out var centreCoord))
        {
            lat = centreCoord.Y;
            lon = centreCoord.X;
        }

        // Use resolved geometry
        if (cache.RelationGeometries.TryGetValue(relation.Id!.Value, out var geom))
        {
            geometry = geom;
            if (lat == 0 && lon == 0)
            {
                var centroid = geom.Centroid;
                lat = centroid.Y;
                lon = centroid.X;
            }
        }

        if (lat == 0 && lon == 0) return null;

        return new Location
        {
            OsmId = relation.Id!.Value,
            OsmType = OsmType.Relation,
            Name = name,
            LocalName = relation.Tags.GetValue("name:local") ?? relation.Tags.GetValue("loc_name"),
            NameEn = relation.Tags.GetValue("name:en"),
            AlternateNames = GetAlternateNames(relation.Tags),
            LocationType = locationType.Value,
            CountryCode = countryCode,
            Lat = lat,
            Lon = lon,
            Geometry = geometry,
            Population = GetPopulation(relation.Tags),
            PostalCode = relation.Tags.GetValue("postal_code") ?? relation.Tags.GetValue("addr:postcode")
        };
    }

    private static LocationType? DetermineLocationType(OsmSharp.Tags.TagsCollectionBase tags)
    {
        if (tags.Contains("boundary", "administrative"))
        {
            var adminLevel = tags.GetValue("admin_level");
            return adminLevel switch
            {
                "2" => LocationType.Country,
                "3" or "4" => LocationType.AdminRegion1,
                "5" or "6" => LocationType.AdminRegion2,
                "7" or "8" => LocationType.AdminRegion3,
                _ => null
            };
        }

        var place = tags.GetValue("place");
        return place switch
        {
            "city" => LocationType.City,
            "town" => LocationType.Town,
            "village" => LocationType.Village,
            "hamlet" or "isolated_dwelling" => LocationType.Locality,
            "suburb" or "neighbourhood" or "quarter" => LocationType.Neighborhood,
            _ => null
        };
    }

    private static string? GetName(OsmSharp.Tags.TagsCollectionBase tags)
    {
        return tags.GetValue("name") ?? tags.GetValue("name:en");
    }

    private static List<string> GetAlternateNames(OsmSharp.Tags.TagsCollectionBase tags)
    {
        var names = new HashSet<string>();
        var primaryName = tags.GetValue("name");

        foreach (var tag in tags)
        {
            if (tag.Key.StartsWith("name:") && tag.Value != primaryName)
                names.Add(tag.Value);
            if (tag.Key == "alt_name" || tag.Key == "old_name" || tag.Key == "short_name")
                names.Add(tag.Value);
        }

        return [..names];
    }

    private static int? GetPopulation(OsmSharp.Tags.TagsCollectionBase tags)
    {
        var pop = tags.GetValue("population");
        if (pop != null && int.TryParse(pop, out var value))
            return value;
        return null;
    }

    private class BoundaryData
    {
        public HashSet<long> RequiredWayIds { get; } = [];
        public HashSet<long> RequiredNodeIds { get; } = [];
        public Dictionary<long, RelationMemberInfo> RelationMembers { get; } = [];
    }

    private class RelationMemberInfo
    {
        public List<long> MemberWayIds { get; } = [];
        public Dictionary<long, string> MemberRoles { get; } = [];
        public long? AdminCentreNodeId { get; set; }
    }

    private class GeometryCache
    {
        public Dictionary<long, Coordinate[]> WayGeometries { get; } = [];
        public Dictionary<long, Geometry> RelationGeometries { get; } = [];
        public Dictionary<long, Coordinate> AdminCentreCoords { get; } = [];
    }
}
