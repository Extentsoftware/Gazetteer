using CommandLine;

namespace Gazetteer.Seeder.Configuration;

public class SeederOptions
{
    [Option('c', "countries", Required = false, Default = "LU",
        HelpText = "Comma-separated ISO country codes to import (e.g., GB,FR,DE). Use 'all' for all EU+UK.")]
    public string Countries { get; set; } = "LU";

    [Option('s', "steps", Required = false, Default = "download,parse,load,index,hierarchy",
        HelpText = "Comma-separated steps to execute: download, parse, load, index, hierarchy")]
    public string Steps { get; set; } = "download,parse,load,index,hierarchy";

    [Option('d', "data-dir", Required = false, Default = "./data",
        HelpText = "Directory to store downloaded data files")]
    public string DataDirectory { get; set; } = "./data";

    [Option("batch-size", Required = false, Default = 5000,
        HelpText = "Batch size for bulk database inserts")]
    public int BatchSize { get; set; } = 5000;

    [Option("recreate-index", Required = false, Default = false,
        HelpText = "Delete and recreate the Elasticsearch index")]
    public bool RecreateIndex { get; set; }
}
