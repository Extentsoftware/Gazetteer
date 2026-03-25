using Gazetteer.Core.Enums;
using Gazetteer.Core.Models;
using Gazetteer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Gazetteer.Seeder.Services;

/// <summary>
/// Synthesizes PostcodeDistrict and PostcodeArea entities by grouping individual Postcode locations.
/// UK: full="SW1A 1AA", outcode/district="SW1A", area="SW"
/// EU varies by country — generally first N digits form a district/area grouping.
/// </summary>
public class PostcodeSynthesizer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PostcodeSynthesizer> _logger;

    // UK postcode regex: area (1-2 letters), district (1-2 digits + optional letter), space, sector+unit
    private static readonly Regex UkPostcodeRegex = new(
        @"^([A-Z]{1,2})(\d{1,2}[A-Z]?)\s*(\d[A-Z]{2})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public PostcodeSynthesizer(IServiceProvider serviceProvider, ILogger<PostcodeSynthesizer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task SynthesizeAsync(string countryCode, CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GazetteerDbContext>();

        var postcodes = await db.Locations
            .Where(l => l.CountryCode == countryCode && l.LocationType == LocationType.Postcode)
            .Select(l => new { l.Name, l.PostalCode, l.Latitude, l.Longitude, l.ParentId })
            .ToListAsync(ct);

        if (postcodes.Count == 0)
        {
            _logger.LogInformation("No postcodes found for {Country}, skipping synthesis", countryCode);
            return;
        }

        _logger.LogInformation("Synthesizing postcode districts/areas from {Count:N0} postcodes for {Country}",
            postcodes.Count, countryCode);

        var districts = new Dictionary<string, List<(double Lat, double Lon, long? ParentId)>>();
        var areas = new Dictionary<string, List<(double Lat, double Lon, long? ParentId)>>();

        foreach (var pc in postcodes)
        {
            var code = (pc.PostalCode ?? pc.Name)?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code)) continue;

            var (district, area) = ExtractPrefixes(code, countryCode);

            if (district != null)
            {
                if (!districts.ContainsKey(district))
                    districts[district] = new();
                districts[district].Add((pc.Latitude, pc.Longitude, pc.ParentId));
            }

            if (area != null)
            {
                if (!areas.ContainsKey(area))
                    areas[area] = new();
                areas[area].Add((pc.Latitude, pc.Longitude, pc.ParentId));
            }
        }

        var newLocations = new List<Location>();

        // Generate a deterministic negative OsmId for synthetic entities
        // Use a hash to avoid collisions
        long syntheticIdBase = -1_000_000_000L;

        foreach (var (district, points) in districts)
        {
            var centroidLat = points.Average(p => p.Lat);
            var centroidLon = points.Average(p => p.Lon);
            var parentId = points.GroupBy(p => p.ParentId).OrderByDescending(g => g.Count()).First().Key;

            newLocations.Add(new Location
            {
                OsmId = syntheticIdBase - Math.Abs(district.GetHashCode()),
                OsmType = OsmType.Synthetic,
                Name = district,
                LocationType = LocationType.PostcodeDistrict,
                CountryCode = countryCode,
                Latitude = centroidLat,
                Longitude = centroidLon,
                PostalCode = district,
                ParentId = parentId
            });
        }

        foreach (var (area, points) in areas)
        {
            if (districts.ContainsKey(area)) continue; // Skip if same as a district

            var centroidLat = points.Average(p => p.Lat);
            var centroidLon = points.Average(p => p.Lon);
            var parentId = points.GroupBy(p => p.ParentId).OrderByDescending(g => g.Count()).First().Key;

            newLocations.Add(new Location
            {
                OsmId = syntheticIdBase - Math.Abs(area.GetHashCode()) - 500_000_000L,
                OsmType = OsmType.Synthetic,
                Name = area,
                LocationType = LocationType.PostcodeArea,
                CountryCode = countryCode,
                Latitude = centroidLat,
                Longitude = centroidLon,
                PostalCode = area,
                ParentId = parentId
            });
        }

        if (newLocations.Count > 0)
        {
            db.Locations.AddRange(newLocations);
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Synthesized {Districts} postcode districts and {Areas} postcode areas for {Country}",
            districts.Count, areas.Count - districts.Count(kv => districts.ContainsKey(kv.Key)), countryCode);
    }

    private static (string? District, string? Area) ExtractPrefixes(string code, string countryCode)
    {
        if (countryCode == "GB")
            return ExtractUkPrefixes(code);

        return ExtractEuPrefixes(code, countryCode);
    }

    private static (string? District, string? Area) ExtractUkPrefixes(string code)
    {
        var match = UkPostcodeRegex.Match(code);
        if (!match.Success) return (null, null);

        var area = match.Groups[1].Value;          // "SW"
        var district = area + match.Groups[2].Value; // "SW1A"
        return (district, area);
    }

    private static (string? District, string? Area) ExtractEuPrefixes(string code, string countryCode)
    {
        // Strip any non-alphanumeric characters
        var clean = Regex.Replace(code, @"[^A-Z0-9]", "", RegexOptions.IgnoreCase);
        if (clean.Length < 3) return (null, null);

        // Country-specific postcode structure
        return countryCode switch
        {
            // 5-digit countries: district = first 3, area = first 2
            "DE" or "FR" or "IT" or "ES" or "AT" or "PL" or "CZ" or "GR" or
            "HR" or "SK" or "RO" or "BG" or "FI" or "SE"
                => (clean[..3], clean[..2]),

            // 4-digit countries: district = first 3, area = first 2
            "BE" or "NL" or "DK" or "HU" or "LU" or "SI" or "CY"
                => (clean.Length >= 3 ? clean[..3] : null, clean[..2]),

            // 3-digit countries (MT): district = first 2, area = first 2
            "MT" => (clean.Length >= 2 ? clean[..2] : null, clean[..2]),

            // Ireland Eircode (A65 F4E2): district = first 3, area = first 3
            "IE" => (clean.Length >= 3 ? clean[..3] : null, clean.Length >= 3 ? clean[..3] : null),

            // Latvia (LV-1001): district = first 3, area = first 2
            "LV" => (clean.Length >= 3 ? clean[..3] : null, clean[..2]),

            // Lithuania (LT-01001): district = first 3, area = first 2
            "LT" => (clean.Length >= 3 ? clean[..3] : null, clean[..2]),

            // Estonia (5 digits)
            "EE" => (clean.Length >= 3 ? clean[..3] : null, clean[..2]),

            // Portugal (4 or 7 digit): district = first 2, area = first 1
            "PT" => (clean.Length >= 2 ? clean[..2] : null, clean[..1]),

            // Default: first 3 and first 2
            _ => (clean.Length >= 3 ? clean[..3] : null, clean.Length >= 2 ? clean[..2] : null)
        };
    }
}
