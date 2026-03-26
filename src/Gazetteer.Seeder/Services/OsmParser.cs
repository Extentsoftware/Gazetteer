using Gazetteer.Core.Enums;
using Gazetteer.Core.Models;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Location = Gazetteer.Core.Models.Location;
using OsmSharp;
using OsmSharp.Streams;

namespace Gazetteer.Seeder.Services;

public class OsmParser
{
    private readonly ILogger<OsmParser> _logger;
    private readonly GeometryFactory _geometryFactory = new(new PrecisionModel(), 4326);

    // Road-type highway values (exclude bus_stop, traffic_signals, platform, etc.)
    private static readonly HashSet<string> RoadHighwayTypes = new()
    {
        "motorway", "trunk", "primary", "secondary", "tertiary",
        "residential", "unclassified", "service", "living_street",
        "pedestrian", "track", "footway", "cycleway", "bridleway", "path",
        "motorway_link", "trunk_link", "primary_link", "secondary_link", "tertiary_link"
    };

    // Key amenity/aeroway/railway values to import
    private static readonly HashSet<string> ImportedAmenities = new()
    {
        "hospital", "clinic", "doctors", "pharmacy",
        "school", "university", "college", "kindergarten",
        "place_of_worship", "library", "community_centre",
        "police", "fire_station", "courthouse",
        "townhall", "post_office"
    };

    private static readonly HashSet<string> ImportedAeroways = new() { "aerodrome" };

    private static readonly HashSet<string> ImportedRailways = new() { "station", "halt" };

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
        ["postcode"] = LocationType.Postcode,
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
        ["9"] = LocationType.Neighborhood,
        ["10"] = LocationType.Neighborhood,
        ["11"] = LocationType.Neighborhood,
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
    /// Pass 2: Three sub-passes to build geometry cache with minimal peak memory.
    ///   2a: Read ways → collect required node IDs (no way node arrays stored)
    ///   2b: Read nodes → collect coordinates for required nodes
    ///   2c: Re-read ways → build geometries directly using node coordinates
    /// </summary>
    private GeometryCache BuildGeometryCache(string pbfFilePath, BoundaryData boundaryData)
    {
        var cache = new GeometryCache();

        // Sub-pass 2a: collect required node IDs only (don't store per-way node arrays)
        // Also harvest addr:postcode from Ways (first node ID for coordinate resolution)
        _logger.LogInformation("Pass 2a: reading ways to collect required node IDs");
        var requiredNodeIds = new HashSet<long>(boundaryData.RequiredNodeIds);
        var postcodeWayNodes = new Dictionary<string, List<long>>(); // postcode -> list of first-node-ids
        int matchedWayCount = 0;
        {
            using var stream = File.OpenRead(pbfFilePath);
            var source = new PBFOsmStreamSource(stream);

            foreach (var osmGeo in source)
            {
                if (osmGeo is not Way way || !way.Id.HasValue || way.Nodes == null || way.Nodes.Length == 0)
                    continue;

                var isBoundaryWay = boundaryData.RequiredWayIds.Contains(way.Id.Value);
                var isNamedHighway = way.Tags != null &&
                    way.Tags.TryGetValue("highway", out var hwVal) &&
                    RoadHighwayTypes.Contains(hwVal) &&
                    way.Tags.ContainsKey("name");
                var isAmenityWay = way.Tags != null &&
                    way.Tags.ContainsKey("name") &&
                    ((way.Tags.TryGetValue("amenity", out var amVal) && ImportedAmenities.Contains(amVal)) ||
                     (way.Tags.TryGetValue("aeroway", out var aeVal) && ImportedAeroways.Contains(aeVal)) ||
                     (way.Tags.TryGetValue("railway", out var rwVal) && ImportedRailways.Contains(rwVal)));

                if (isBoundaryWay || isNamedHighway || isAmenityWay)
                {
                    // Add node IDs directly to the set instead of storing the array
                    foreach (var nodeId in way.Nodes)
                        requiredNodeIds.Add(nodeId);
                    matchedWayCount++;
                }

                // Harvest addr:postcode from Ways — store first node ID for later coordinate resolution
                if (way.Tags != null && way.Tags.TryGetValue("addr:postcode", out var wayPostcode))
                {
                    var normalized = wayPostcode.Trim().ToUpperInvariant();
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        if (!postcodeWayNodes.TryGetValue(normalized, out var nodeList))
                        {
                            nodeList = new List<long>();
                            postcodeWayNodes[normalized] = nodeList;
                        }
                        requiredNodeIds.Add(way.Nodes[0]);
                        nodeList.Add(way.Nodes[0]);
                    }
                }
            }
        }

        _logger.LogInformation("Pass 2a complete: {Ways} ways referencing {Nodes} unique nodes",
            matchedWayCount, requiredNodeIds.Count);

        // Sub-pass 2b: read only the nodes we need
        var nodeCoords = new Dictionary<long, (double lat, double lon)>(requiredNodeIds.Count);
        _logger.LogInformation("Pass 2b: reading {Count} required node coordinates", requiredNodeIds.Count);
        {
            using var stream = File.OpenRead(pbfFilePath);
            var source = new PBFOsmStreamSource(stream);
            int found = 0;
            int targetCount = requiredNodeIds.Count;

            foreach (var osmGeo in source)
            {
                if (osmGeo is Node node && node.Id.HasValue &&
                    node.Latitude.HasValue && node.Longitude.HasValue &&
                    requiredNodeIds.Contains(node.Id.Value))
                {
                    nodeCoords[node.Id.Value] = (node.Latitude.Value, node.Longitude.Value);
                    found++;

                    // Early exit once we have all needed nodes
                    if (found == targetCount) break;
                }
            }
        }

        _logger.LogInformation("Pass 2b complete: cached {Count} node coordinates", nodeCoords.Count);

        // Free the requiredNodeIds set — no longer needed, nodeCoords has the lookup
        requiredNodeIds.Clear();
        requiredNodeIds.TrimExcess();

        // Sub-pass 2c: re-read ways to build geometries directly using nodeCoords
        _logger.LogInformation("Pass 2c: building way geometries");
        {
            using var stream = File.OpenRead(pbfFilePath);
            var source = new PBFOsmStreamSource(stream);

            foreach (var osmGeo in source)
            {
                if (osmGeo is not Way way || !way.Id.HasValue || way.Nodes == null || way.Nodes.Length == 0)
                    continue;

                var isBoundaryWay = boundaryData.RequiredWayIds.Contains(way.Id.Value);
                var isNamedHighway = way.Tags != null &&
                    way.Tags.TryGetValue("highway", out var hwVal) &&
                    RoadHighwayTypes.Contains(hwVal) &&
                    way.Tags.ContainsKey("name");
                var isAmenityWay = way.Tags != null &&
                    way.Tags.ContainsKey("name") &&
                    ((way.Tags.TryGetValue("amenity", out var amVal) && ImportedAmenities.Contains(amVal)) ||
                     (way.Tags.TryGetValue("aeroway", out var aeVal) && ImportedAeroways.Contains(aeVal)) ||
                     (way.Tags.TryGetValue("railway", out var rwVal) && ImportedRailways.Contains(rwVal)));

                if (isBoundaryWay || isNamedHighway || isAmenityWay)
                {
                    var coords = new List<Coordinate>();
                    foreach (var nodeId in way.Nodes)
                    {
                        if (nodeCoords.TryGetValue(nodeId, out var coord))
                            coords.Add(new Coordinate(coord.lon, coord.lat));
                    }

                    if (coords.Count >= 2)
                        cache.WayGeometries[way.Id.Value] = [.. coords];
                }
            }
        }

        _logger.LogInformation("Pass 2c complete: built {Count} way geometries", cache.WayGeometries.Count);

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

        // Resolve postcode Way coordinates into the cache
        foreach (var (postcode, nodeIds) in postcodeWayNodes)
        {
            double sumLat = 0, sumLon = 0;
            int count = 0;
            foreach (var nodeId in nodeIds)
            {
                if (nodeCoords.TryGetValue(nodeId, out var coord))
                {
                    sumLat += coord.lat;
                    sumLon += coord.lon;
                    count++;
                }
            }
            if (count > 0)
                cache.PostcodeWayCoords[postcode] = (sumLat / count, sumLon / count, count);
        }

        _logger.LogInformation("Resolved coordinates for {Count} postcodes from Ways", cache.PostcodeWayCoords.Count);

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

                polygons.Add(_geometryFactory.CreatePolygon(shell, [.. holes]));
            }

            if (polygons.Count == 1)
                return polygons[0];

            return _geometryFactory.CreateMultiPolygon([.. polygons]);
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
        if (segments.Count == 0) return [];

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
                        current.AddRange(segment.AsEnumerable().Reverse().Skip(1));
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
                        var prepend = segment.AsEnumerable().Reverse().Take(segment.Length - 1).ToList();
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
                rings.Add([.. current]);
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
        var roadGroups = new Dictionary<string, List<Location>>();
        // Buffer places for deduplication (same name+type appears as both Node and Relation)
        var placeGroups = new Dictionary<string, List<Location>>();
        // Harvest unique postcodes from addr:postcode tags (keyed by normalized postcode)
        var harvestedPostcodes = new Dictionary<string, (double Lat, double Lon, int Count)>();
        var knownPostcodes = new HashSet<string>(); // Track postcodes already emitted as place=postcode entities

        foreach (var osmGeo in source)
        {
            nodeCount++;
            if (nodeCount % 1_000_000 == 0)
                _logger.LogInformation("  Processed {Count:N0} OSM elements, extracted {Extracted:N0} locations",
                    nodeCount, extractedCount);

            // Harvest addr:postcode from ANY element with coordinates (for postcode synthesis)
            if (osmGeo.Tags != null && osmGeo.Tags.TryGetValue("addr:postcode", out var addrPostcode))
            {
                var normalized = addrPostcode.Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(normalized))
                {
                    double pcLat = 0, pcLon = 0;
                    if (osmGeo is Node pcNode && pcNode.Latitude.HasValue && pcNode.Longitude.HasValue)
                    {
                        pcLat = pcNode.Latitude.Value;
                        pcLon = pcNode.Longitude.Value;
                    }

                    if (harvestedPostcodes.TryGetValue(normalized, out var existing))
                    {
                        // Running average of coordinates
                        if (pcLat != 0 || pcLon != 0)
                        {
                            var newCount = existing.Count + 1;
                            var newLat = existing.Lat + (pcLat - existing.Lat) / newCount;
                            var newLon = existing.Lon + (pcLon - existing.Lon) / newCount;
                            harvestedPostcodes[normalized] = (newLat, newLon, newCount);
                        }
                    }
                    else
                    {
                        harvestedPostcodes[normalized] = (pcLat, pcLon, 1);
                    }
                }
            }

            var location = TryExtractLocation(osmGeo, countryCode, geometryCache);
            if (location == null) continue;

            extractedCount++;

            if (location.LocationType == LocationType.Road)
            {
                var key = location.Name.ToUpperInvariant();
                if (!roadGroups.TryGetValue(key, out var group))
                {
                    group = new List<Location>();
                    roadGroups[key] = group;
                }
                group.Add(location);
            }
            else if (location.LocationType == LocationType.Postcode)
            {
                // Track postcodes — emit immediately (no dedup needed, they have unique names)
                if (!string.IsNullOrEmpty(location.PostalCode))
                    knownPostcodes.Add(location.PostalCode.Trim().ToUpperInvariant());

                yield return location;
            }
            else
            {
                // Buffer places for deduplication (same place as Node + Relation)
                var placeKey = $"{location.Name.ToUpperInvariant()}|{location.LocationType}";
                if (!placeGroups.TryGetValue(placeKey, out var placeGroup))
                {
                    placeGroup = new List<Location>();
                    placeGroups[placeKey] = placeGroup;
                }
                placeGroup.Add(location);
            }
        }

        // Deduplicate places (same name+type as both Node and Relation)
        _logger.LogInformation("Deduplicating {Groups:N0} place groups from {Total:N0} place entries",
            placeGroups.Count, placeGroups.Values.Sum(g => g.Count));

        foreach (var (_, group) in placeGroups)
        {
            yield return PickBestPlace(group);
        }

        // Merge road segments with the same name into single entries
        _logger.LogInformation("Merging {Groups:N0} unique road names from {Total:N0} road segments",
            roadGroups.Count, roadGroups.Values.Sum(g => g.Count));

        foreach (var (_, segments) in roadGroups)
        {
            // Sub-cluster by geographic proximity — same name doesn't mean same road
            foreach (var cluster in ClusterByProximity(segments, maxDistanceKm: 2.0))
            {
                yield return MergeRoadSegments(cluster);
            }
        }

        // Merge Way-harvested postcode coordinates into the running averages
        foreach (var (code, wayCoord) in geometryCache.PostcodeWayCoords)
        {
            if (harvestedPostcodes.TryGetValue(code, out var existing))
            {
                // Only update if the existing entry has no coordinates
                if (existing.Lat == 0 && existing.Lon == 0)
                    harvestedPostcodes[code] = (wayCoord.Lat, wayCoord.Lon, existing.Count + wayCoord.Count);
                // else keep the Node-based average (more accurate, from actual addresses)
            }
            else
            {
                harvestedPostcodes[code] = (wayCoord.Lat, wayCoord.Lon, wayCoord.Count);
            }
        }

        // Emit harvested postcodes that weren't already extracted as proper place=postcode entities
        var newPostcodes = harvestedPostcodes
            .Where(kv => !knownPostcodes.Contains(kv.Key))
            .ToList();

        _logger.LogInformation("Harvested {Total:N0} unique postcodes from addr:postcode tags, {New:N0} are new",
            harvestedPostcodes.Count, newPostcodes.Count);

        long syntheticPostcodeId = -2_000_000_000L;
        foreach (var (code, coords) in newPostcodes)
        {
            yield return new Location
            {
                OsmId = syntheticPostcodeId--,
                OsmType = OsmType.Synthetic,
                Name = code,
                LocationType = LocationType.Postcode,
                CountryCode = countryCode,
                Latitude = coords.Lat,
                Longitude = coords.Lon,
                PostalCode = code
            };
        }

        _logger.LogInformation("Finished parsing {File}: {Total:N0} elements, {Extracted:N0} locations, {Postcodes:N0} harvested postcodes",
            pbfFilePath, nodeCount, extractedCount, newPostcodes.Count);
    }

    private Location MergeRoadSegments(List<Location> segments)
    {
        var first = segments[0];
        if (segments.Count == 1) return first;

        // Average centroid from all segments with valid coordinates
        var withCoords = segments.Where(s => s.Latitude != 0 || s.Longitude != 0).ToList();
        if (withCoords.Count > 0)
        {
            first.Latitude = withCoords.Average(s => s.Latitude);
            first.Longitude = withCoords.Average(s => s.Longitude);
        }

        // Merge geometries into a MultiLineString
        var lineStrings = segments
            .Where(s => s.Geometry is LineString)
            .Select(s => (LineString)s.Geometry!)
            .ToArray();
        if (lineStrings.Length > 0)
        {
            first.Geometry = _geometryFactory.CreateMultiLineString(lineStrings);
        }

        // Merge unique postal codes
        var postcodes = segments
            .Where(s => !string.IsNullOrEmpty(s.PostalCode))
            .SelectMany(s => s.PostalCode!.Split(';'))
            .Distinct()
            .ToList();
        if (postcodes.Count > 0)
            first.PostalCode = string.Join(";", postcodes);

        // Merge unique alternate names
        var altNames = segments
            .Where(s => !string.IsNullOrEmpty(s.AlternateNames))
            .SelectMany(s => s.AlternateNames!.Split(';'))
            .Distinct()
            .ToList();
        if (altNames.Count > 0)
            first.AlternateNames = string.Join(";", altNames);

        // Use first available LocalName/NameEn
        first.LocalName ??= segments.FirstOrDefault(s => s.LocalName != null)?.LocalName;
        first.NameEn ??= segments.FirstOrDefault(s => s.NameEn != null)?.NameEn;

        return first;
    }

    /// <summary>
    /// Picks the best representative from duplicate place entries (e.g., Node + Relation for same town).
    /// Prefers: Relation > Way > Node (relations have boundaries/geometry).
    /// Merges coordinates and alternate names from all duplicates.
    /// </summary>
    private static Location PickBestPlace(List<Location> duplicates)
    {
        if (duplicates.Count == 1) return duplicates[0];

        // Prefer Relation (has geometry), then Way, then Node
        var best = duplicates
            .OrderByDescending(d => d.OsmType switch
            {
                OsmType.Relation => 3,
                OsmType.Way => 2,
                OsmType.Node => 1,
                _ => 0
            })
            .ThenByDescending(d => d.Geometry != null) // prefer one with geometry
            .ThenByDescending(d => d.Latitude != 0 || d.Longitude != 0) // prefer one with coords
            .ThenByDescending(d => d.Population ?? 0) // prefer one with population
            .First();

        // If the best doesn't have coordinates, grab from another
        if (best.Latitude == 0 && best.Longitude == 0)
        {
            var withCoords = duplicates.FirstOrDefault(d => d.Latitude != 0 || d.Longitude != 0);
            if (withCoords != null)
            {
                best.Latitude = withCoords.Latitude;
                best.Longitude = withCoords.Longitude;
            }
        }

        // Merge alternate names from all duplicates
        var allAltNames = duplicates
            .Where(d => !string.IsNullOrEmpty(d.AlternateNames))
            .SelectMany(d => d.AlternateNames!.Split(';'))
            .Distinct()
            .ToList();
        if (allAltNames.Count > 0)
            best.AlternateNames = string.Join(";", allAltNames);

        // Use first available fields from any duplicate
        best.LocalName ??= duplicates.FirstOrDefault(d => d.LocalName != null)?.LocalName;
        best.NameEn ??= duplicates.FirstOrDefault(d => d.NameEn != null)?.NameEn;
        best.Population ??= duplicates.FirstOrDefault(d => d.Population.HasValue)?.Population;
        best.PostalCode ??= duplicates.FirstOrDefault(d => d.PostalCode != null)?.PostalCode;

        return best;
    }

    /// <summary>
    /// Clusters road segments by geographic proximity so that roads with the same name
    /// in different areas (e.g., "High Beeches" in Orpington vs Buckinghamshire) stay separate.
    /// </summary>
    private static List<List<Location>> ClusterByProximity(List<Location> segments, double maxDistanceKm)
    {
        var clusters = new List<List<Location>>();

        foreach (var segment in segments)
        {
            if (segment.Latitude == 0 && segment.Longitude == 0)
            {
                // No coords — can't cluster, emit as standalone
                clusters.Add(new List<Location> { segment });
                continue;
            }

            List<Location>? bestCluster = null;
            double bestDist = double.MaxValue;

            foreach (var cluster in clusters)
            {
                foreach (var member in cluster)
                {
                    if (member.Latitude == 0 && member.Longitude == 0) continue;
                    var dist = HaversineKm(segment.Latitude, segment.Longitude, member.Latitude, member.Longitude);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCluster = cluster;
                    }
                }
            }

            if (bestCluster != null && bestDist <= maxDistanceKm)
                bestCluster.Add(segment);
            else
                clusters.Add(new List<Location> { segment });
        }

        return clusters;
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private Location? TryExtractLocation(OsmGeo osmGeo, string countryCode, GeometryCache geometryCache)
    {
        if (osmGeo.Tags == null || !osmGeo.Tags.ContainsKey("name"))
            return null;

        var locationType = DetermineLocationType(osmGeo);
        if (locationType == null)
            return null;

        var name = osmGeo.Tags.TryGetValue("name", out var nameVal) ? nameVal : string.Empty;
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

                    // Build polygon for closed ways (amenities, buildings)
                    if (wayCoords.Length >= 4 &&
                        wayCoords[0].Equals2D(wayCoords[^1]))
                    {
                        try
                        {
                            geometry = _geometryFactory.CreatePolygon(wayCoords);
                        }
                        catch
                        {
                            // Invalid polygon — skip geometry, keep centroid
                        }
                    }
                    // Build LineString for open ways (roads, paths)
                    else if (wayCoords.Length >= 2)
                    {
                        try
                        {
                            geometry = _geometryFactory.CreateLineString(wayCoords);
                        }
                        catch
                        {
                            // Invalid linestring — skip geometry, keep centroid
                        }
                    }
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
            LocalName = osmGeo.Tags.TryGetValue("name:local", out var localName) ? localName : null,
            NameEn = osmGeo.Tags.TryGetValue("name:en", out var nameEn) ? nameEn : null,
            LocationType = locationType.Value,
            CountryCode = countryCode,
            Latitude = lat,
            Longitude = lon,
            Geometry = geometry,
            Population = ParsePopulation(osmGeo.Tags.TryGetValue("population", out var pop) ? pop : null),
            PostalCode = (osmGeo.Tags.TryGetValue("addr:postcode", out var postcode) ? postcode : null)
                         ?? (osmGeo.Tags.TryGetValue("postal_code", out var postalCode) ? postalCode : null)
                         ?? (locationType.Value == LocationType.Postcode ? name : null),
        };

        // Set SubType for amenities
        if (locationType.Value == LocationType.Amenity)
        {
            if (osmGeo.Tags.TryGetValue("amenity", out var amenityVal))
                location.SubType = amenityVal;
            else if (osmGeo.Tags.TryGetValue("aeroway", out var aerowayVal))
                location.SubType = aerowayVal;
            else if (osmGeo.Tags.TryGetValue("railway", out var railwayVal))
                location.SubType = railwayVal;
        }

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

        // Check named roads (only actual road types, not bus stops, traffic signals, etc.)
        if (osmGeo.Tags.TryGetValue("highway", out var hwType) &&
            osmGeo.Tags.ContainsKey("name") &&
            RoadHighwayTypes.Contains(hwType))
            return LocationType.Road;

        // Check key amenities (hospitals, schools, etc.)
        if (osmGeo.Tags.ContainsKey("name"))
        {
            if (osmGeo.Tags.TryGetValue("amenity", out var amenity) && ImportedAmenities.Contains(amenity))
                return LocationType.Amenity;
            if (osmGeo.Tags.TryGetValue("aeroway", out var aeroway) && ImportedAeroways.Contains(aeroway))
                return LocationType.Amenity;
            if (osmGeo.Tags.TryGetValue("railway", out var railway) && ImportedRailways.Contains(railway))
                return LocationType.Amenity;
        }

        return null;
    }

    private static long? ParsePopulation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string([.. value.Where(c => char.IsDigit(c))]);
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

    private sealed class BoundaryData
    {
        public HashSet<long> RequiredWayIds { get; } = [];
        public HashSet<long> RequiredNodeIds { get; } = [];
        public Dictionary<long, RelationMemberInfo> RelationMembers { get; } = [];
    }

    private sealed class RelationMemberInfo
    {
        public List<long> MemberWayIds { get; init; } = [];
        public Dictionary<long, string> MemberRoles { get; init; } = [];
        public long? AdminCentreNodeId { get; init; }
    }

    private sealed class GeometryCache
    {
        public Dictionary<long, Coordinate[]> WayGeometries { get; } = [];
        public Dictionary<long, Geometry> RelationGeometries { get; } = [];
        public Dictionary<long, (double lat, double lon)> AdminCentreCoords { get; } = [];
        public Dictionary<string, (double Lat, double Lon, int Count)> PostcodeWayCoords { get; } = [];
    }
}
