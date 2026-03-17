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
    private readonly GeometryFactory _geometryFactory = new(new PrecisionModel(), 4326);

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

        // Pass 1: Scan relations to collect member way IDs for admin boundaries,
        //         and scan those ways to collect their node IDs.
        //         PBF order is nodes → ways → relations, so we scan relations first
        //         (to know which ways we need), then re-read for ways.
        var boundaryData = ScanBoundaryRelations(pbfFilePath);
        _logger.LogInformation("Pass 1 complete: found {Relations} boundary relations referencing {Ways} ways",
            boundaryData.RelationMembers.Count, boundaryData.RequiredWayIds.Count);

        // Pass 2: Read ways to get node ID lists, then read nodes for only those IDs.
        //         This avoids caching ALL node coordinates (billions for large countries).
        var geometryCache = BuildGeometryCache(pbfFilePath, boundaryData);
        _logger.LogInformation("Geometry cache built: {Ways} way geometries, {Relations} relation geometries",
            geometryCache.WayGeometries.Count, geometryCache.RelationGeometries.Count);

        // Pass 3: Final parse — extract locations with resolved geometries
        return ParseLocations(pbfFilePath, countryCode, geometryCache);
    }

    /// <summary>
    /// Pass 1: Scan only relations to collect boundary member way/node IDs.
    /// </summary>
    private BoundaryData ScanBoundaryRelations(string pbfFilePath)
    {
        var data = new BoundaryData();

        using var stream = File.OpenRead(pbfFilePath);
        var source = new PBFOsmStreamSource(stream);

        foreach (var osmGeo in source)
        {
            if (osmGeo is not Relation relation) continue;
            if (relation.Tags == null || relation.Members == null) continue;

            // Only care about administrative boundaries
            if (!relation.Tags.TryGetValue("boundary", out var boundary)) continue;
            if (boundary != "administrative" && boundary != "postal_code") continue;

            var memberWayIds = new List<long>();
            var memberRoles = new Dictionary<long, string>();
            long? adminCentreNodeId = null;

            foreach (var member in relation.Members)
            {
                if (member.Type == OsmGeoType.Way)
                {
                    memberWayIds.Add(member.Id);
                    data.RequiredWayIds.Add(member.Id);
                    memberRoles[member.Id] = member.Role ?? "";
                }
                else if (member.Type == OsmGeoType.Node && member.Role == "admin_centre")
                {
                    adminCentreNodeId = member.Id;
                    data.RequiredNodeIds.Add(member.Id);
                }
            }

            data.RelationMembers[relation.Id!.Value] = new RelationMemberInfo
            {
                MemberWayIds = memberWayIds,
                MemberRoles = memberRoles,
                AdminCentreNodeId = adminCentreNodeId
            };
        }

        return data;
    }

    /// <summary>
    /// Pass 2: Two sub-passes to build geometry cache without storing all node coordinates.
    ///   2a: Read ways referenced by boundary relations → collect their node ID lists
    ///   2b: Read only the nodes referenced by those ways → collect coordinates
    /// </summary>
    private GeometryCache BuildGeometryCache(string pbfFilePath, BoundaryData boundaryData)
    {
        var cache = new GeometryCache();
        var wayNodeIds = new Dictionary<long, long[]>();

        // Sub-pass 2a: collect node ID lists for boundary ways
        _logger.LogInformation("Pass 2a: reading boundary ways to collect node references");
        {
            using var stream = File.OpenRead(pbfFilePath);
            var source = new PBFOsmStreamSource(stream);

            foreach (var osmGeo in source)
            {
                if (osmGeo is Way way && way.Id.HasValue &&
                    boundaryData.RequiredWayIds.Contains(way.Id.Value) &&
                    way.Nodes != null && way.Nodes.Length > 0)
                {
                    wayNodeIds[way.Id.Value] = way.Nodes;
                }
            }
        }

        // Build the set of node IDs we actually need (only boundary way nodes + admin centres)
        var requiredNodeIds = new HashSet<long>(boundaryData.RequiredNodeIds);
        foreach (var nodeIds in wayNodeIds.Values)
        {
            foreach (var nodeId in nodeIds)
                requiredNodeIds.Add(nodeId);
        }

        _logger.LogInformation("Pass 2a complete: {Ways} ways referencing {Nodes} unique nodes",
            wayNodeIds.Count, requiredNodeIds.Count);

        // Sub-pass 2b: read only the nodes we need
        var nodeCoords = new Dictionary<long, (double lat, double lon)>(requiredNodeIds.Count);
        _logger.LogInformation("Pass 2b: reading {Count} required node coordinates", requiredNodeIds.Count);
        {
            using var stream = File.OpenRead(pbfFilePath);
            var source = new PBFOsmStreamSource(stream);
            int found = 0;

            foreach (var osmGeo in source)
            {
                if (osmGeo is Node node && node.Id.HasValue &&
                    node.Latitude.HasValue && node.Longitude.HasValue &&
                    requiredNodeIds.Contains(node.Id.Value))
                {
                    nodeCoords[node.Id.Value] = (node.Latitude.Value, node.Longitude.Value);
                    found++;

                    // Early exit once we have all needed nodes
                    if (found == requiredNodeIds.Count) break;
                }
            }
        }

        _logger.LogInformation("Pass 2b complete: cached {Count} node coordinates", nodeCoords.Count);

        // Build way line geometries
        foreach (var (wayId, nodeIds) in wayNodeIds)
        {
            var coords = new List<Coordinate>();
            foreach (var nodeId in nodeIds)
            {
                if (nodeCoords.TryGetValue(nodeId, out var coord))
                    coords.Add(new Coordinate(coord.lon, coord.lat));
            }

            if (coords.Count >= 2)
                cache.WayGeometries[wayId] = coords.ToArray();
        }

        // Build relation polygon geometries by assembling member ways
        foreach (var (relationId, memberInfo) in boundaryData.RelationMembers)
        {
            var polygon = BuildRelationGeometry(memberInfo, cache);
            if (polygon != null)
                cache.RelationGeometries[relationId] = polygon;

            // Also store admin_centre coordinates for centroid fallback
            if (memberInfo.AdminCentreNodeId.HasValue &&
                nodeCoords.TryGetValue(memberInfo.AdminCentreNodeId.Value, out var centreCoord))
            {
                cache.AdminCentreCoords[relationId] = centreCoord;
            }
        }

        return cache;
    }

    /// <summary>
    /// Assemble a polygon from a relation's member ways.
    /// Outer ways form the shell, inner ways form holes.
    /// </summary>
    private Geometry? BuildRelationGeometry(RelationMemberInfo memberInfo, GeometryCache cache)
    {
        var outerWayCoords = new List<Coordinate[]>();
        var innerWayCoords = new List<Coordinate[]>();

        foreach (var wayId in memberInfo.MemberWayIds)
        {
            if (!cache.WayGeometries.TryGetValue(wayId, out var coords)) continue;

            var role = memberInfo.MemberRoles.GetValueOrDefault(wayId, "");
            if (role == "inner")
                innerWayCoords.Add(coords);
            else // "outer" or "" (default to outer)
                outerWayCoords.Add(coords);
        }

        if (outerWayCoords.Count == 0) return null;

        try
        {
            // Merge outer way segments into closed rings
            var outerRings = MergeWaySegments(outerWayCoords);
            var innerRings = MergeWaySegments(innerWayCoords);

            if (outerRings.Count == 0) return null;

            var polygons = new List<Polygon>();
            foreach (var outerRing in outerRings)
            {
                // Find inner rings contained within this outer ring
                var holes = new List<LinearRing>();
                var shell = _geometryFactory.CreateLinearRing(outerRing);

                foreach (var innerRing in innerRings)
                {
                    var hole = _geometryFactory.CreateLinearRing(innerRing);
                    if (shell.EnvelopeInternal.Contains(hole.EnvelopeInternal))
                        holes.Add(hole);
                }

                polygons.Add(_geometryFactory.CreatePolygon(shell, holes.ToArray()));
            }

            if (polygons.Count == 1)
                return polygons[0];

            return _geometryFactory.CreateMultiPolygon(polygons.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to build geometry for relation: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Merge disconnected way segments into closed rings.
    /// Ways in OSM boundary relations are often split into segments that need
    /// to be joined end-to-end to form a complete ring.
    /// </summary>
    private List<Coordinate[]> MergeWaySegments(List<Coordinate[]> segments)
    {
        if (segments.Count == 0) return new List<Coordinate[]>();

        var rings = new List<Coordinate[]>();
        var remaining = new List<Coordinate[]>(segments);

        while (remaining.Count > 0)
        {
            var current = new List<Coordinate>(remaining[0]);
            remaining.RemoveAt(0);

            bool merged = true;
            while (merged && !IsRingClosed(current))
            {
                merged = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    var segment = remaining[i];
                    if (segment.Length == 0) continue;

                    var currentEnd = current[^1];
                    var currentStart = current[0];
                    var segStart = segment[0];
                    var segEnd = segment[^1];

                    if (CoordinatesMatch(currentEnd, segStart))
                    {
                        // Append segment (skip first point to avoid duplicate)
                        current.AddRange(segment.Skip(1));
                        remaining.RemoveAt(i);
                        merged = true;
                        break;
                    }
                    else if (CoordinatesMatch(currentEnd, segEnd))
                    {
                        // Append reversed segment
                        current.AddRange(segment.Reverse().Skip(1));
                        remaining.RemoveAt(i);
                        merged = true;
                        break;
                    }
                    else if (CoordinatesMatch(currentStart, segEnd))
                    {
                        // Prepend segment
                        var prepend = segment.Take(segment.Length - 1).ToList();
                        prepend.AddRange(current);
                        current = prepend;
                        remaining.RemoveAt(i);
                        merged = true;
                        break;
                    }
                    else if (CoordinatesMatch(currentStart, segStart))
                    {
                        // Prepend reversed segment
                        var prepend = segment.Reverse().Take(segment.Length - 1).ToList();
                        prepend.AddRange(current);
                        current = prepend;
                        remaining.RemoveAt(i);
                        merged = true;
                        break;
                    }
                }
            }

            // Close the ring if endpoints are close but not exact
            if (current.Count >= 4 && !IsRingClosed(current))
            {
                if (CoordinatesNear(current[0], current[^1]))
                    current[^1] = current[0]; // snap to close
                else
                    current.Add(current[0]); // force close
            }

            if (current.Count >= 4 && IsRingClosed(current))
                rings.Add(current.ToArray());
        }

        return rings;
    }

    private static bool IsRingClosed(List<Coordinate> coords)
        => coords.Count >= 4 && coords[0].Equals2D(coords[^1]);

    private static bool CoordinatesMatch(Coordinate a, Coordinate b)
        => a.Equals2D(b);

    private static bool CoordinatesNear(Coordinate a, Coordinate b, double tolerance = 1e-7)
        => Math.Abs(a.X - b.X) < tolerance && Math.Abs(a.Y - b.Y) < tolerance;

    /// <summary>
    /// Pass 3: Parse all locations, attaching resolved geometries where available.
    /// </summary>
    private IEnumerable<Location> ParseLocations(string pbfFilePath, string countryCode, GeometryCache geometryCache)
    {
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

            var location = TryExtractLocation(osmGeo, countryCode, geometryCache);
            if (location != null)
            {
                extractedCount++;
                yield return location;
            }
        }

        _logger.LogInformation("Finished parsing {File}: {Total:N0} elements, {Extracted:N0} locations",
            pbfFilePath, nodeCount, extractedCount);
    }

    private Location? TryExtractLocation(OsmGeo osmGeo, string countryCode, GeometryCache geometryCache)
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
        Geometry? geometry = null;

        switch (osmGeo)
        {
            case Node node when node.Latitude.HasValue && node.Longitude.HasValue:
                lat = node.Latitude.Value;
                lon = node.Longitude.Value;
                osmType = OsmType.Node;
                break;

            case Way way when way.Id.HasValue:
                osmType = OsmType.Way;
                // Compute centroid from way geometry if available
                if (geometryCache.WayGeometries.TryGetValue(way.Id.Value, out var wayCoords) && wayCoords.Length >= 2)
                {
                    var centroid = ComputeCentroid(wayCoords);
                    lat = centroid.lat;
                    lon = centroid.lon;
                }
                break;

            case Relation relation when relation.Id.HasValue:
                osmType = OsmType.Relation;
                // Use resolved polygon geometry for admin boundaries
                if (geometryCache.RelationGeometries.TryGetValue(relation.Id.Value, out var relGeometry))
                {
                    geometry = relGeometry;
                    var centroid = relGeometry.Centroid;
                    lat = centroid.Y;
                    lon = centroid.X;
                }
                // Fall back to admin_centre coordinates
                else if (geometryCache.AdminCentreCoords.TryGetValue(relation.Id.Value, out var centreCoord))
                {
                    lat = centreCoord.lat;
                    lon = centreCoord.lon;
                }
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
            Geometry = geometry,
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
        var cleaned = new string(value.Where(c => char.IsDigit(c)).ToArray());
        return long.TryParse(cleaned, out var pop) ? pop : null;
    }

    private static (double lat, double lon) ComputeCentroid(Coordinate[] coords)
    {
        double sumLat = 0, sumLon = 0;
        foreach (var c in coords)
        {
            sumLat += c.Y;
            sumLon += c.X;
        }
        return (sumLat / coords.Length, sumLon / coords.Length);
    }

    // ---- Internal data structures ----

    private class BoundaryData
    {
        public HashSet<long> RequiredWayIds { get; } = new();
        public HashSet<long> RequiredNodeIds { get; } = new();
        public Dictionary<long, RelationMemberInfo> RelationMembers { get; } = new();
    }

    private class RelationMemberInfo
    {
        public List<long> MemberWayIds { get; init; } = new();
        public Dictionary<long, string> MemberRoles { get; init; } = new();
        public long? AdminCentreNodeId { get; init; }
    }

    private class GeometryCache
    {
        public Dictionary<long, Coordinate[]> WayGeometries { get; } = new();
        public Dictionary<long, Geometry> RelationGeometries { get; } = new();
        public Dictionary<long, (double lat, double lon)> AdminCentreCoords { get; } = new();
    }
}
