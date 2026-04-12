using System.Net;
using System.Text.Json;
using Monitor.Server.Models;

namespace Monitor.Server.Services;

public sealed class PexelsService(IHttpClientFactory httpClientFactory)
{
    private static readonly string[] AllowedProxyHosts =
    [
        "images.pexels.com",
        "player.vimeo.com",
        "videos.pexels.com"
    ];

    public async Task<PexelsSearchResponseSnapshot> SearchAsync(
        string apiKey,
        string mediaKind,
        string query,
        int page,
        int perPage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Add a Pexels API key before searching.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new PexelsSearchResponseSnapshot(mediaKind, string.Empty, 1, perPage, 0, null, null, []);
        }

        page = Math.Max(1, page);
        perPage = Math.Clamp(perPage, 1, 80);
        var normalizedKind = string.Equals(mediaKind, "video", StringComparison.OrdinalIgnoreCase) ? "video" : "image";
        var endpoint = normalizedKind == "video"
            ? $"https://api.pexels.com/v1/videos/search?query={Uri.EscapeDataString(query)}&page={page}&per_page={perPage}"
            : $"https://api.pexels.com/v1/search?query={Uri.EscapeDataString(query)}&page={page}&per_page={perPage}";

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        using var response = await httpClientFactory.CreateClient(nameof(PexelsService)).SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;

        return normalizedKind == "video"
            ? ParseVideos(query, page, perPage, root)
            : ParsePhotos(query, page, perPage, root);
    }

    public bool IsAllowedProxyUrl(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return AllowedProxyHosts.Any(host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    private static PexelsSearchResponseSnapshot ParsePhotos(string query, int page, int perPage, JsonElement root)
    {
        var results = new List<PexelsAssetSnapshot>();
        foreach (var photo in root.GetProperty("photos").EnumerateArray())
        {
            var src = photo.GetProperty("src");
            var renderUrl = src.TryGetProperty("large2x", out var large2x)
                ? large2x.GetString()
                : src.GetProperty("large").GetString();
            var previewUrl = src.TryGetProperty("medium", out var medium)
                ? medium.GetString()
                : src.GetProperty("large").GetString();
            var photographer = photo.GetProperty("photographer").GetString() ?? "Unknown";
            var photographerUrl = photo.GetProperty("photographer_url").GetString() ?? "https://www.pexels.com";
            var pexelsUrl = photo.GetProperty("url").GetString() ?? photographerUrl;

            results.Add(new PexelsAssetSnapshot(
                photo.GetProperty("id").GetInt64().ToString(),
                "image",
                photo.TryGetProperty("alt", out var alt) && !string.IsNullOrWhiteSpace(alt.GetString())
                    ? alt.GetString()!
                    : photographer,
                previewUrl ?? string.Empty,
                $"/api/media/pexels/stream?url={Uri.EscapeDataString(renderUrl ?? previewUrl ?? string.Empty)}",
                photo.GetProperty("width").GetInt32(),
                photo.GetProperty("height").GetInt32(),
                null,
                $"Photo by {photographer} on Pexels",
                photographerUrl,
                pexelsUrl));
        }

        return new PexelsSearchResponseSnapshot(
            "image",
            query,
            page,
            perPage,
            root.TryGetProperty("total_results", out var totalResults) ? totalResults.GetInt32() : results.Count,
            root.TryGetProperty("prev_page", out var prevPage) ? prevPage.GetString() : null,
            root.TryGetProperty("next_page", out var nextPage) ? nextPage.GetString() : null,
            results);
    }

    private static PexelsSearchResponseSnapshot ParseVideos(string query, int page, int perPage, JsonElement root)
    {
        var results = new List<PexelsAssetSnapshot>();
        foreach (var video in root.GetProperty("videos").EnumerateArray())
        {
            var renderFile = SelectVideoFile(video);
            if (renderFile.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            var user = video.GetProperty("user");
            var author = user.GetProperty("name").GetString() ?? "Unknown";
            var authorUrl = user.GetProperty("url").GetString() ?? "https://www.pexels.com";
            var pexelsUrl = video.GetProperty("url").GetString() ?? authorUrl;
            var previewUrl = video.TryGetProperty("image", out var image) ? image.GetString() : string.Empty;
            var rawLink = renderFile.GetProperty("link").GetString() ?? string.Empty;

            results.Add(new PexelsAssetSnapshot(
                video.GetProperty("id").GetInt64().ToString(),
                "video",
                $"Pexels video #{video.GetProperty("id").GetInt64()}",
                previewUrl ?? string.Empty,
                $"/api/media/pexels/stream?url={Uri.EscapeDataString(rawLink)}",
                video.GetProperty("width").GetInt32(),
                video.GetProperty("height").GetInt32(),
                video.TryGetProperty("duration", out var duration) ? duration.GetInt32() : null,
                $"Video by {author} on Pexels",
                authorUrl,
                pexelsUrl));
        }

        return new PexelsSearchResponseSnapshot(
            "video",
            query,
            page,
            perPage,
            root.TryGetProperty("total_results", out var totalResults) ? totalResults.GetInt32() : results.Count,
            root.TryGetProperty("prev_page", out var prevPage) ? prevPage.GetString() : null,
            root.TryGetProperty("next_page", out var nextPage) ? nextPage.GetString() : null,
            results);
    }

    private static JsonElement SelectVideoFile(JsonElement video)
    {
        if (!video.TryGetProperty("video_files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        JsonElement best = default;
        var bestScore = -1L;
        foreach (var file in files.EnumerateArray())
        {
            var fileType = file.TryGetProperty("file_type", out var type) ? type.GetString() : string.Empty;
            if (!string.Equals(fileType, "video/mp4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var width = file.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
            var height = file.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;
            var score = (long)width * height;
            if (score <= bestScore)
            {
                continue;
            }

            best = file;
            bestScore = score;
        }

        return best;
    }
}
