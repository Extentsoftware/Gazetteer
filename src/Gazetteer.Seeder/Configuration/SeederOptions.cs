namespace Gazetteer.Seeder.Configuration;

public class SeederOptions
{
    public string Countries { get; set; } = "all";
    public string Steps { get; set; } = "download,parse,load,index,hierarchy";
    public string DataDirectory { get; set; } = "./data";
    public int BatchSize { get; set; } = 5000;
    public bool RecreateIndex { get; set; }

    /// <summary>
    /// When true, ignores any recorded seed checkpoints and always reprocesses every
    /// requested country, even if the source file hasn't changed since it was last seeded.
    /// </summary>
    public bool ForceReseed { get; set; }

    public string? OnspdFile { get; set; }
    public string OnspdUrl { get; set; } = "https://www.arcgis.com/sharing/rest/content/items/3080229224424c9cb53c0b48f5a64d27/data";
}
