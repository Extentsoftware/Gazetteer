using Microsoft.Extensions.Logging;

namespace Gazetteer.Seeder.Services;

public class PbfDownloader
{
    private readonly ILogger<PbfDownloader> _logger;
    private readonly HttpClient _http;

    public PbfDownloader(ILogger<PbfDownloader> logger)
    {
        _logger = logger;
        _http = new HttpClient();
    }

    public async Task<string> DownloadAsync(string geofabrikPath, string dataDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dataDir);

        var fileName = Path.GetFileName(geofabrikPath) + "-latest.osm.pbf";
        var filePath = Path.Combine(dataDir, fileName);

        if (File.Exists(filePath))
        {
            _logger.LogInformation("PBF file already exists: {Path}", filePath);
            return filePath;
        }

        var url = $"https://download.geofabrik.de/{geofabrikPath}-latest.osm.pbf";
        _logger.LogInformation("Downloading {Url}...", url);

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        var lastReport = DateTime.UtcNow;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
            {
                var pct = totalBytes > 0 ? (double)totalRead / totalBytes * 100 : 0;
                _logger.LogInformation("Download progress: {Read:F1} MB / {Total:F1} MB ({Pct:F1}%)",
                    totalRead / 1048576.0, (totalBytes ?? 0) / 1048576.0, pct);
                lastReport = DateTime.UtcNow;
            }
        }

        _logger.LogInformation("Download complete: {Path} ({Size:F1} MB)", filePath, totalRead / 1048576.0);
        return filePath;
    }
}
