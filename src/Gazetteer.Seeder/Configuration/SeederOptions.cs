namespace Gazetteer.Seeder.Configuration;

public class SeederOptions
{
    public string Countries { get; set; } = "all";
    public string Steps { get; set; } = "download,parse,load,index,hierarchy";
    public string DataDirectory { get; set; } = "./data";
    public int BatchSize { get; set; } = 5000;
    public bool RecreateIndex { get; set; }
    public string? OnspdFile { get; set; }
}
