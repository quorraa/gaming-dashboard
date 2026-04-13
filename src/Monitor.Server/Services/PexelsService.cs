using System.Net;
using System.Text.Json;
using Monitor.Server.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Monitor.Server.Services;

public sealed class PexelsService(IHttpClientFactory httpClientFactory, IMemoryCache cache)
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

    public async Task<string> ResolveVideoProxyUrlAsync(
        string apiKey,
        string assetId,
        int targetWidth,
        int targetHeight,
        double devicePixelRatio,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Add a Pexels API key before using video backgrounds.");
        }

        if (string.IsNullOrWhiteSpace(assetId))
        {
            throw new InvalidOperationException("Pexels video id is required.");
        }

        var bucket = ResolveVideoBucket(targetWidth, targetHeight, devicePixelRatio);
        var cacheKey = $"pexels-video-link:{assetId}:{bucket}";
        if (cache.TryGetValue(cacheKey, out string? cachedLink) && !string.IsNullOrWhiteSpace(cachedLink))
        {
            return cachedLink;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.pexels.com/videos/videos/{Uri.EscapeDataString(assetId)}");
        request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        using var response = await httpClientFactory.CreateClient(nameof(PexelsService)).SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var best = SelectVideoFile(json.RootElement, bucket);
        if (best.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("No compatible Pexels video stream was found.");
        }

        var rawLink = best.GetProperty("link").GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawLink))
        {
            throw new InvalidOperationException("Pexels returned an empty video link.");
        }

        cache.Set(cacheKey, rawLink, TimeSpan.FromHours(6));
        return rawLink;
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
            var renderFile = SelectVideoFile(video, 1920);
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

    private static JsonElement SelectVideoFile(JsonElement video, int targetMaxDimension)
    {
        if (!video.TryGetProperty("video_files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        JsonElement best = default;
        long bestScore = long.MaxValue;
        long bestArea = long.MaxValue;
        foreach (var file in files.EnumerateArray())
        {
            var fileType = file.TryGetProperty("file_type", out var type) ? type.GetString() : string.Empty;
            if (!string.Equals(fileType, "video/mp4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var width = file.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
            var height = file.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;
            var maxDimension = Math.Max(width, height);
            if (maxDimension <= 0)
            {
                continue;
            }

            var distance = maxDimension <= targetMaxDimension
                ? targetMaxDimension - maxDimension
                : (maxDimension - targetMaxDimension) + 100_000;
            var area = (long)width * height;
            if (distance > bestScore || (distance == bestScore && area >= bestArea))
            {
                continue;
            }

            best = file;
            bestScore = distance;
            bestArea = area;
        }

        return best;
    }

    private static int ResolveVideoBucket(int targetWidth, int targetHeight, double devicePixelRatio)
    {
        var scale = Math.Clamp(devicePixelRatio, 1d, 2d);
        var maxDimension = (int)Math.Ceiling(Math.Max(Math.Max(targetWidth, targetHeight), 1) * scale);
        if (maxDimension <= 960) return 960;
        if (maxDimension <= 1280) return 1280;
        if (maxDimension <= 1920) return 1920;
        return 2560;
    }
}
