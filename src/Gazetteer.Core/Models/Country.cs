namespace Gazetteer.Core.Models;

public class Country
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Continent { get; set; } = string.Empty;

    /// <summary>
    /// LastWriteTimeUtc of the source PBF file at the time this country was last fully
    /// seeded (parse+load+hierarchy+postcodes). Used to skip re-seeding on restart when
    /// the on-disk file hasn't changed since.
    /// </summary>
    public DateTime? LastSeededFileTimestampUtc { get; set; }

    /// <summary>
    /// When this country was last fully (re)seeded.
    /// </summary>
    public DateTime? LastSeededAtUtc { get; set; }
}
