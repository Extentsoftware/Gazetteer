namespace Gazetteer.Seeder.Configuration;

public class SeederOptions
{
    public string[] Countries { get; set; } = [];
    public string[] Steps { get; set; } = ["download", "parse", "load", "hierarchy", "index"];
    public string DataDir { get; set; } = "data";
    public int BatchSize { get; set; } = 5000;
    public bool RecreateIndex { get; set; }
}
