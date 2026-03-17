using Gazetteer.Seeder.Configuration;
using Microsoft.Extensions.Logging;

namespace Gazetteer.Seeder.Services;

public class PbfDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PbfDownloader> _logger;

    public PbfDownloader(HttpClient httpClient, ILogger<PbfDownloader> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> DownloadAsync(string countryCode, string dataDir, CancellationToken ct = default)
    {
        if (!CountryConfig.EuUkCountries.TryGetValue(countryCode, out var countryInfo))
            throw new ArgumentException($"Unknown country code: {countryCode}");

        var url = CountryConfig.GetDownloadUrl(countryInfo.GeofabrikPath);
        var filePath = Path.Combine(dataDir, $"{countryCode.ToLowerInvariant()}-latest.osm.pbf");

        if (File.Exists(filePath))
        {
            _logger.LogInformation("File already exists: {FilePath}, skipping download", filePath);
            return filePath;
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

        _logger.LogInformation("Downloaded {Country}: {Size:N0} bytes to {Path}", countryInfo.Name, totalRead, filePath);
        return filePath;
    }
}
