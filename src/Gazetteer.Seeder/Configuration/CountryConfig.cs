namespace Gazetteer.Seeder.Configuration;

public static class CountryConfig
{
    public static readonly Dictionary<string, CountryInfo> EuUkCountries = new()
    {
        ["AT"] = new("Austria", "europe/austria"),
        ["BE"] = new("Belgium", "europe/belgium"),
        ["BG"] = new("Bulgaria", "europe/bulgaria"),
        ["HR"] = new("Croatia", "europe/croatia"),
        ["CY"] = new("Cyprus", "europe/cyprus"),
        ["CZ"] = new("Czech Republic", "europe/czech-republic"),
        ["DK"] = new("Denmark", "europe/denmark"),
        ["EE"] = new("Estonia", "europe/estonia"),
        ["FI"] = new("Finland", "europe/finland"),
        ["FR"] = new("France", "europe/france"),
        ["DE"] = new("Germany", "europe/germany"),
        ["GR"] = new("Greece", "europe/greece"),
        ["HU"] = new("Hungary", "europe/hungary"),
        ["IE"] = new("Ireland and Northern Ireland", "europe/ireland-and-northern-ireland"),
        ["IT"] = new("Italy", "europe/italy"),
        ["LV"] = new("Latvia", "europe/latvia"),
        ["LT"] = new("Lithuania", "europe/lithuania"),
        ["LU"] = new("Luxembourg", "europe/luxembourg"),
        ["MT"] = new("Malta", "europe/malta"),
        ["NL"] = new("Netherlands", "europe/netherlands"),
        ["PL"] = new("Poland", "europe/poland"),
        ["PT"] = new("Portugal", "europe/portugal"),
        ["RO"] = new("Romania", "europe/romania"),
        ["SK"] = new("Slovakia", "europe/slovakia"),
        ["SI"] = new("Slovenia", "europe/slovenia"),
        ["ES"] = new("Spain", "europe/spain"),
        ["SE"] = new("Sweden", "europe/sweden"),
        ["GB"] = new("United Kingdom", "europe/great-britain"),
    };

    public static string GetDownloadUrl(string geofabrikPath) =>
        $"https://download.geofabrik.de/{geofabrikPath}-latest.osm.pbf";

    public static IEnumerable<string> ParseCountryCodes(string input)
    {
        if (string.Equals(input, "all", StringComparison.OrdinalIgnoreCase))
            return EuUkCountries.Keys;

        return input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToUpperInvariant())
            .Where(c => EuUkCountries.ContainsKey(c));
    }
}

public record CountryInfo(string Name, string GeofabrikPath);
