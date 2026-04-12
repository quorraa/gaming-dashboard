using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monitor.Server.Models;

namespace Monitor.Server.Services.Spotify;

public sealed class SpotifyService(
    DashboardPreferencesStore preferencesStore,
    IHttpClientFactory httpClientFactory,
    ILogger<SpotifyService> logger)
{
    private static readonly string[] Scopes =
    [
        "user-read-playback-state",
        "user-modify-playback-state",
        "user-read-currently-playing",
        "user-library-read",
        "user-library-modify"
    ];

    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string _accessToken = string.Empty;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    public string BuildRedirectUri(HttpRequest request) => $"{request.Scheme}://{request.Host}/api/spotify/connect/callback";

    public Uri CreateAuthorizationUri(HttpRequest request, string? returnUrl)
    {
        var preferences = preferencesStore.Current.Spotify;
        if (string.IsNullOrWhiteSpace(preferences.ClientId))
        {
            throw new InvalidOperationException("Enter a Spotify client ID first.");
        }

        var codeVerifier = GenerateOpaqueToken(72);
        var state = GenerateOpaqueToken(32);
        var redirectUri = BuildRedirectUri(request);
        var normalizedReturnUrl = NormalizeReturnUrl(returnUrl);
        _pending[state] = new PendingAuthorization(codeVerifier, redirectUri, normalizedReturnUrl);

        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = preferences.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = codeChallenge,
            ["scope"] = string.Join(' ', Scopes),
            ["state"] = state
        };

        return new Uri($"https://accounts.spotify.com/authorize?{BuildQuery(query)}");
    }

    public async Task<string> CompleteAuthorizationAsync(
        string code,
        string state,
        CancellationToken cancellationToken)
    {
        if (!_pending.TryRemove(state, out var pending))
        {
            throw new InvalidOperationException("Spotify authorization expired. Start connect again.");
        }

        var preferences = preferencesStore.Current.Spotify;
        if (string.IsNullOrWhiteSpace(preferences.ClientId))
        {
            throw new InvalidOperationException("Spotify client ID is missing.");
        }

        var payload = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = pending.RedirectUri,
            ["client_id"] = preferences.ClientId,
            ["code_verifier"] = pending.CodeVerifier
        };

        var token = await ExchangeTokenAsync(payload, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new InvalidOperationException("Spotify did not return a refresh token.");
        }

        UpdateTokenCache(token.AccessToken, token.ExpiresInSeconds);
        preferencesStore.SetSpotifyAuthorization(token.RefreshToken);
        return pending.ReturnUrl;
    }

    public async Task<SpotifySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var preferences = preferencesStore.Current.Spotify;
        if (!preferences.Enabled)
        {
            return SpotifySnapshot.Disabled("Spotify integration disabled.");
        }

        if (string.IsNullOrWhiteSpace(preferences.ClientId))
        {
            return new SpotifySnapshot(true, "setup", null, "Enter a Spotify client ID to enable Spotify controls.");
        }

        if (!preferences.IsAuthorized || string.IsNullOrWhiteSpace(preferences.RefreshToken))
        {
            return new SpotifySnapshot(true, "setup", null, "Connect Spotify to control playback.");
        }

        try
        {
            var accessToken = await EnsureAccessTokenAsync(cancellationToken);
            using var response = await SendApiAsync(HttpMethod.Get, "/me/player", accessToken, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return new SpotifySnapshot(true, "connected", null, "No active Spotify playback.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                InvalidateAccessToken();
                return new SpotifySnapshot(true, "reauth-required", null, "Reconnect Spotify to resume playback controls.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new SpotifySnapshot(true, "warning", null, await DescribeErrorAsync(response, cancellationToken));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var nowPlaying = await ParseNowPlayingAsync(accessToken, document.RootElement, cancellationToken);
            if (nowPlaying is null)
            {
                return new SpotifySnapshot(true, "connected", null, "No active Spotify playback.");
            }

            return new SpotifySnapshot(true, "connected", nowPlaying, null);
        }
        catch (InvalidOperationException ex)
        {
            return new SpotifySnapshot(true, "warning", null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Spotify read failed.");
            return new SpotifySnapshot(true, "warning", null, "Spotify did not respond.");
        }
    }

    public async Task ExecuteCommandAsync(SpotifyCommandRequest command, CancellationToken cancellationToken)
    {
        var preferences = preferencesStore.Current.Spotify;
        if (string.IsNullOrWhiteSpace(preferences.ClientId) || string.IsNullOrWhiteSpace(preferences.RefreshToken))
        {
            throw new InvalidOperationException("Connect Spotify first.");
        }

        var accessToken = await EnsureAccessTokenAsync(cancellationToken);
        var action = command.Action?.Trim().ToLowerInvariant();
        switch (action)
        {
            case "previous":
                await SendCommandAsync(HttpMethod.Post, BuildPlayerPath("/me/player/previous", command.DeviceId), accessToken, cancellationToken);
                break;
            case "next":
                await SendCommandAsync(HttpMethod.Post, BuildPlayerPath("/me/player/next", command.DeviceId), accessToken, cancellationToken);
                break;
            case "play":
            case "resume":
                await SendCommandAsync(HttpMethod.Put, BuildPlayerPath("/me/player/play", command.DeviceId), accessToken, cancellationToken);
                break;
            case "pause":
                await SendCommandAsync(HttpMethod.Put, BuildPlayerPath("/me/player/pause", command.DeviceId), accessToken, cancellationToken);
                break;
            case "shuffle":
            {
                var enabled = command.Value.GetValueOrDefault() >= 0.5d;
                await SendCommandAsync(HttpMethod.Put, BuildPlayerPath("/me/player/shuffle", command.DeviceId, new Dictionary<string, string?>
                {
                    ["state"] = enabled ? "true" : "false"
                }), accessToken, cancellationToken);
                break;
            }
            case "repeat":
            {
                var repeatState = command.RepeatState?.Trim().ToLowerInvariant();
                repeatState = repeatState is "track" or "context" or "off" ? repeatState : "off";
                await SendCommandAsync(HttpMethod.Put, BuildPlayerPath("/me/player/repeat", command.DeviceId, new Dictionary<string, string?>
                {
                    ["state"] = repeatState
                }), accessToken, cancellationToken);
                break;
            }
            case "seek":
            {
                var positionMs = Math.Max(0, (int)Math.Round(command.Value.GetValueOrDefault()));
                await SendCommandAsync(HttpMethod.Put, BuildPlayerPath("/me/player/seek", command.DeviceId, new Dictionary<string, string?>
                {
                    ["position_ms"] = positionMs.ToString()
                }), accessToken, cancellationToken);
                break;
            }
            case "volume":
            {
                var volumePercent = Math.Clamp((int)Math.Round(command.Value.GetValueOrDefault()), 0, 100);
                await SendCommandAsync(HttpMethod.Put, BuildPlayerPath("/me/player/volume", command.DeviceId, new Dictionary<string, string?>
                {
                    ["volume_percent"] = volumePercent.ToString()
                }), accessToken, cancellationToken);
                break;
            }
            case "like":
                await SendLibraryCommandAsync(HttpMethod.Put, command.ItemId, accessToken, cancellationToken);
                break;
            case "unlike":
                await SendLibraryCommandAsync(HttpMethod.Delete, command.ItemId, accessToken, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Unsupported Spotify command.");
        }
    }

    private async Task SendLibraryCommandAsync(
        HttpMethod method,
        string? itemId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new InvalidOperationException("No active Spotify track is available.");
        }

        await SendCommandAsync(method, $"/me/tracks?ids={Uri.EscapeDataString(itemId)}", accessToken, cancellationToken);
    }

    private async Task<SpotifyNowPlayingSnapshot?> ParseNowPlayingAsync(
        string accessToken,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var mediaType = root.TryGetProperty("currently_playing_type", out var typeElement)
            ? typeElement.GetString() ?? "track"
            : "track";
        var itemId = item.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        var title = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "Unknown title" : "Unknown title";
        var trackUrl = ReadNestedString(item, "external_urls", "spotify");
        var artistNames = new List<string>();
        var artistUrl = string.Empty;
        if (item.TryGetProperty("artists", out var artistsElement) && artistsElement.ValueKind == JsonValueKind.Array)
        {
            var artists = artistsElement.EnumerateArray().ToArray();
            foreach (var artist in artists)
            {
                if (artist.TryGetProperty("name", out var artistNameElement))
                {
                    artistNames.Add(artistNameElement.GetString() ?? string.Empty);
                }
            }

            artistUrl = artists.Length > 0 ? ReadNestedString(artists[0], "external_urls", "spotify") : string.Empty;
        }

        var album = item.TryGetProperty("album", out var albumElement) ? albumElement : default;
        var albumName = album.ValueKind == JsonValueKind.Object && album.TryGetProperty("name", out var albumNameElement)
            ? albumNameElement.GetString() ?? string.Empty
            : string.Empty;
        var albumUrl = album.ValueKind == JsonValueKind.Object ? ReadNestedString(album, "external_urls", "spotify") : string.Empty;
        var coverUrl = string.Empty;
        if (album.ValueKind == JsonValueKind.Object
            && album.TryGetProperty("images", out var imagesElement)
            && imagesElement.ValueKind == JsonValueKind.Array)
        {
            coverUrl = imagesElement.EnumerateArray()
                .Select(image => image.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;
        }

        var isLiked = mediaType == "track"
            && !string.IsNullOrWhiteSpace(itemId)
            && await IsTrackLikedAsync(accessToken, itemId, cancellationToken);

        var device = root.TryGetProperty("device", out var deviceElement) ? deviceElement : default;
        var volume = device.ValueKind == JsonValueKind.Object && device.TryGetProperty("volume_percent", out var volumeElement)
            ? volumeElement.GetInt32()
            : 0;
        var supportsVolume = device.ValueKind == JsonValueKind.Object
            && device.TryGetProperty("supports_volume", out var supportsVolumeElement)
            && supportsVolumeElement.ValueKind == JsonValueKind.True;

        return new SpotifyNowPlayingSnapshot(
            itemId,
            mediaType,
            title,
            string.Join(", ", artistNames.Where(name => !string.IsNullOrWhiteSpace(name))),
            albumName,
            coverUrl,
            trackUrl,
            artistUrl,
            albumUrl,
            root.TryGetProperty("is_playing", out var playingElement) && playingElement.ValueKind == JsonValueKind.True,
            root.TryGetProperty("shuffle_state", out var shuffleElement) && shuffleElement.ValueKind == JsonValueKind.True,
            root.TryGetProperty("repeat_state", out var repeatElement) ? repeatElement.GetString() ?? "off" : "off",
            root.TryGetProperty("progress_ms", out var progressElement) && progressElement.TryGetInt32(out var progressMs) ? progressMs : 0,
            item.TryGetProperty("duration_ms", out var durationElement) && durationElement.TryGetInt32(out var durationMs) ? durationMs : 0,
            volume,
            device.ValueKind == JsonValueKind.Object && device.TryGetProperty("id", out var deviceIdElement) ? deviceIdElement.GetString() ?? string.Empty : string.Empty,
            device.ValueKind == JsonValueKind.Object && device.TryGetProperty("name", out var deviceNameElement) ? deviceNameElement.GetString() ?? string.Empty : string.Empty,
            supportsVolume,
            isLiked);
    }

    private async Task<bool> IsTrackLikedAsync(string accessToken, string itemId, CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(
            HttpMethod.Get,
            $"/me/tracks/contains?ids={Uri.EscapeDataString(itemId)}",
            accessToken,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.ValueKind == JsonValueKind.Array
            && document.RootElement.GetArrayLength() > 0
            && document.RootElement[0].ValueKind == JsonValueKind.True;
    }

    private async Task<string> EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
            {
                return _accessToken;
            }

            var preferences = preferencesStore.Current.Spotify;
            if (string.IsNullOrWhiteSpace(preferences.RefreshToken))
            {
                throw new InvalidOperationException("Connect Spotify first.");
            }

            var payload = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = preferences.RefreshToken,
                ["client_id"] = preferences.ClientId
            };

            var token = await ExchangeTokenAsync(payload, cancellationToken);
            UpdateTokenCache(token.AccessToken, token.ExpiresInSeconds);
            if (!string.IsNullOrWhiteSpace(token.RefreshToken) && !string.Equals(token.RefreshToken, preferences.RefreshToken, StringComparison.Ordinal))
            {
                preferencesStore.SetSpotifyAuthorization(token.RefreshToken);
            }

            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<SpotifyTokenResponse> ExchangeTokenAsync(
        IReadOnlyDictionary<string, string> payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = new FormUrlEncodedContent(payload)
        };
        using var response = await httpClientFactory.CreateClient(nameof(SpotifyService)).SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (raw.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                preferencesStore.ClearSpotifyAuthorization();
                InvalidateAccessToken();
            }

            throw new InvalidOperationException("Spotify authorization failed. Reconnect Spotify.");
        }

        using var document = JsonDocument.Parse(raw);
        var accessToken = document.RootElement.TryGetProperty("access_token", out var accessElement)
            ? accessElement.GetString() ?? string.Empty
            : string.Empty;
        var refreshToken = document.RootElement.TryGetProperty("refresh_token", out var refreshElement)
            ? refreshElement.GetString() ?? string.Empty
            : string.Empty;
        var expiresInSeconds = document.RootElement.TryGetProperty("expires_in", out var expiresElement)
            ? expiresElement.GetInt32()
            : 3600;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Spotify authorization did not return an access token.");
        }

        return new SpotifyTokenResponse(accessToken, refreshToken, expiresInSeconds);
    }

    private async Task<HttpResponseMessage> SendApiAsync(
        HttpMethod method,
        string path,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, $"https://api.spotify.com/v1{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await httpClientFactory.CreateClient(nameof(SpotifyService))
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task SendCommandAsync(
        HttpMethod method,
        string path,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"https://api.spotify.com/v1{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClientFactory.CreateClient(nameof(SpotifyService))
            .SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            InvalidateAccessToken();
            throw new InvalidOperationException("Spotify session expired. Reconnect Spotify.");
        }

        throw new InvalidOperationException(await DescribeErrorAsync(response, cancellationToken));
    }

    private static string BuildPlayerPath(
        string basePath,
        string? deviceId,
        IReadOnlyDictionary<string, string?>? extraQuery = null)
    {
        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["device_id"] = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId
        };

        if (extraQuery is not null)
        {
            foreach (var pair in extraQuery)
            {
                query[pair.Key] = pair.Value;
            }
        }

        var queryString = BuildQuery(query);
        return string.IsNullOrWhiteSpace(queryString)
            ? basePath
            : $"{basePath}?{queryString}";
    }

    private async Task<string> DescribeErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "Spotify rejected the command. Spotify Premium may be required.",
            HttpStatusCode.NotFound => "No active Spotify device is available.",
            (HttpStatusCode)429 => "Spotify rate-limited the request. Try again in a moment.",
            _ => $"Spotify returned {(int)response.StatusCode}."
        };

        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.Object
                    && errorElement.TryGetProperty("message", out var messageElement))
                {
                    return messageElement.GetString() ?? fallback;
                }

                if (errorElement.ValueKind == JsonValueKind.String)
                {
                    return errorElement.GetString() ?? fallback;
                }
            }

            return fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void UpdateTokenCache(string accessToken, int expiresInSeconds)
    {
        _accessToken = accessToken;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresInSeconds - 60));
    }

    private void InvalidateAccessToken()
    {
        _accessToken = string.Empty;
        _accessTokenExpiresAt = DateTimeOffset.MinValue;
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/studio/index.html";
        }

        var trimmed = returnUrl.Trim();
        if (!trimmed.StartsWith('/'))
        {
            return "/studio/index.html";
        }

        return trimmed;
    }

    private static string GenerateOpaqueToken(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64UrlEncode(bytes);
    }

    private static string ComputeCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string BuildQuery(IReadOnlyDictionary<string, string?> values) => string.Join("&",
        values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));

    private static string ReadNestedString(JsonElement element, string parentProperty, string childProperty)
    {
        if (!element.TryGetProperty(parentProperty, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return parent.TryGetProperty(childProperty, out var child)
            ? child.GetString() ?? string.Empty
            : string.Empty;
    }

    private sealed record PendingAuthorization(string CodeVerifier, string RedirectUri, string ReturnUrl);
    private sealed record SpotifyTokenResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds);
}
