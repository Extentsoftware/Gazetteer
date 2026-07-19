using Gazetteer.Seeder.Configuration;
using Microsoft.Extensions.Logging;

namespace Gazetteer.Seeder.Services;

public class PbfDownloader
{
    /// <summary>
    /// A local file younger than this is assumed to still be fresh, so the upstream
    /// freshness check (HEAD request to Geofabrik) is skipped entirely to avoid
    /// unnecessary network calls on every seeder run.
    /// </summary>
    private static readonly TimeSpan MaxFileAgeBeforeFreshnessCheck = TimeSpan.FromDays(30);

    private readonly HttpClient _httpClient;
    private readonly ILogger<PbfDownloader> _logger;

    public PbfDownloader(HttpClient httpClient, ILogger<PbfDownloader> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Downloads the country's PBF file, skipping the download if a local copy already
    /// exists and appears up to date.
    /// </summary>
    /// <param name="seedCheckpointUtc">
    /// The LastWriteTimeUtc of the file that was used the last time this country was
    /// successfully seeded (if known). If the file on disk is older than this, it was
    /// evidently swapped for a stale/partial copy since then, so it is re-downloaded.
    /// </param>
    public async Task<string> DownloadAsync(string countryCode, string dataDir, DateTime? seedCheckpointUtc = null, CancellationToken ct = default)
    {
        if (!CountryConfig.EuUkCountries.TryGetValue(countryCode, out var countryInfo))
            throw new ArgumentException($"Unknown country code: {countryCode}");

        var url = CountryConfig.GetDownloadUrl(countryInfo.GeofabrikPath);
        var filePath = Path.Combine(dataDir, $"{countryCode.ToLowerInvariant()}-latest.osm.pbf");

        if (File.Exists(filePath))
        {
            var localTimestampUtc = File.GetLastWriteTimeUtc(filePath);

            var fileAge = DateTime.UtcNow - localTimestampUtc;

            if (seedCheckpointUtc is not null && localTimestampUtc < seedCheckpointUtc)
            {
                _logger.LogWarning(
                    "Local file {FilePath} ({LocalDate:u}) is older than the seed checkpoint recorded for {Country} ({Checkpoint:u}); re-downloading",
                    filePath, localTimestampUtc, countryCode, seedCheckpointUtc);
            }
            else if (fileAge <= MaxFileAgeBeforeFreshnessCheck)
            {
                _logger.LogInformation(
                    "File already exists and is only {AgeDays:N0} day(s) old (< {ThresholdDays:N0}-day freshness check threshold): {FilePath}, skipping download",
                    fileAge.TotalDays, MaxFileAgeBeforeFreshnessCheck.TotalDays, filePath);
                return filePath;
            }
            else if (await IsRemoteNewerAsync(url, localTimestampUtc, countryCode, ct))
            {
                _logger.LogInformation("Newer {Country} file available upstream; re-downloading", countryCode);
            }
            else
            {
                _logger.LogInformation("File already exists and is up to date: {FilePath}, skipping download", filePath);
                return filePath;
            }
        }

        Directory.CreateDirectory(dataDir);
        _logger.LogInformation("Downloading {Country} from {Url}...", countryInfo.Name, url);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        var lastProgress = 0;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            if (totalBytes > 0)
            {
                var progress = (int)(totalRead * 100 / totalBytes);
                if (progress >= lastProgress + 10)
                {
                    lastProgress = progress;
                    _logger.LogInformation("  {Country}: {Progress}% ({Read:N0} / {Total:N0} bytes)",
                        countryCode, progress, totalRead, totalBytes);
                }
            }
        }

        fileStream.Close();

        // Preserve the upstream Last-Modified date on disk (rather than "now") so future
        // freshness comparisons reflect the actual source data date, not download time.
        if (response.Content.Headers.LastModified is DateTimeOffset lastModified)
        {
            File.SetLastWriteTimeUtc(filePath, lastModified.UtcDateTime);
        }

        _logger.LogInformation("Downloaded {Country}: {Size:N0} bytes to {Path}", countryInfo.Name, totalRead, filePath);
        return filePath;
    }

    /// <summary>
    /// Issues a HEAD request to check whether the upstream file has been updated since
    /// our local copy's timestamp. Failures are treated as "not newer" so a flaky HEAD
    /// request doesn't force an unnecessary re-download or abort the run.
    /// </summary>
    private async Task<bool> IsRemoteNewerAsync(string url, DateTime localTimestampUtc, string countryCode, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.LastModified is DateTimeOffset lastModified)
            {
                return lastModified.UtcDateTime > localTimestampUtc;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check upstream freshness for {Country}; keeping local file", countryCode);
            return false;
        }
    }
}
