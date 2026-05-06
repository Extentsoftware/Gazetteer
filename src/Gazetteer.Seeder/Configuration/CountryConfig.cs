namespace Gazetteer.Seeder.Configuration;

public record CountryInfo(string Code, string Name, string GeofabrikPath);

public static class CountryConfig
{
    public static readonly Dictionary<string, CountryInfo> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AT"] = new("AT", "Austria", "europe/austria"),
        ["BE"] = new("BE", "Belgium", "europe/belgium"),
        ["BG"] = new("BG", "Bulgaria", "europe/bulgaria"),
        ["HR"] = new("HR", "Croatia", "europe/croatia"),
        ["CY"] = new("CY", "Cyprus", "europe/cyprus"),
        ["CZ"] = new("CZ", "Czech Republic", "europe/czech-republic"),
        ["DK"] = new("DK", "Denmark", "europe/denmark"),
        ["EE"] = new("EE", "Estonia", "europe/estonia"),
        ["FI"] = new("FI", "Finland", "europe/finland"),
        ["FR"] = new("FR", "France", "europe/france"),
        ["DE"] = new("DE", "Germany", "europe/germany"),
        ["GR"] = new("GR", "Greece", "europe/greece"),
        ["HU"] = new("HU", "Hungary", "europe/hungary"),
        ["IE"] = new("IE", "Ireland", "europe/ireland-and-northern-ireland"),
        ["IT"] = new("IT", "Italy", "europe/italy"),
        ["LV"] = new("LV", "Latvia", "europe/latvia"),
        ["LT"] = new("LT", "Lithuania", "europe/lithuania"),
        ["LU"] = new("LU", "Luxembourg", "europe/luxembourg"),
        ["MT"] = new("MT", "Malta", "europe/malta"),
        ["NL"] = new("NL", "Netherlands", "europe/netherlands"),
        ["PL"] = new("PL", "Poland", "europe/poland"),
        ["PT"] = new("PT", "Portugal", "europe/portugal"),
        ["RO"] = new("RO", "Romania", "europe/romania"),
        ["SK"] = new("SK", "Slovakia", "europe/slovakia"),
        ["SI"] = new("SI", "Slovenia", "europe/slovenia"),
        ["ES"] = new("ES", "Spain", "europe/spain"),
        ["SE"] = new("SE", "Sweden", "europe/sweden"),
        ["GB"] = new("GB", "United Kingdom", "europe/great-britain"),
    };
}
