using System.Text.Json;
using Monitor.Server.Models;

namespace Monitor.Server.Services;

public sealed class ThemeMediaService
{
    private const string AppDataFolderName = "GamingDashboard";
    private const string MediaFolderName = "Media";
    private const string ManifestFileName = "media.library.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".m4v"
    };

    private readonly Lock _lock = new();
    private readonly ILogger<ThemeMediaService> _logger;
    private readonly string _mediaRoot;
    private readonly string _manifestPath;
    private List<MediaLibraryEntry> _entries;

    public ThemeMediaService(IHostEnvironment environment, ILogger<ThemeMediaService> logger)
    {
        _logger = logger;
        var appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appDataRoot))
        {
            appDataRoot = environment.ContentRootPath;
        }

        var baseRoot = Path.Combine(appDataRoot, AppDataFolderName);
        _mediaRoot = Path.Combine(baseRoot, MediaFolderName);
        _manifestPath = Path.Combine(baseRoot, ManifestFileName);
        Directory.CreateDirectory(_mediaRoot);
        _entries = LoadManifest();
    }

    public IReadOnlyList<MediaAssetSnapshot> ListAssets()
    {
        lock (_lock)
        {
            PruneMissingFilesUnsafe();
            return _entries
                .OrderByDescending(entry => entry.AddedAt)
                .Select(ToSnapshot)
                .ToArray();
        }
    }

    public async Task<MediaAssetSnapshot> SaveUploadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
        {
            throw new InvalidOperationException("Uploaded file is empty.");
        }

        var extension = Path.GetExtension(file.FileName);
        var mediaKind = DetectMediaKind(extension, file.ContentType);
        if (mediaKind == "none")
        {
            throw new InvalidOperationException("Only image and video uploads are supported.");
        }

        var id = Guid.NewGuid().ToString("N");
        var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(file.FileName));
        var storedFileName = $"{id}-{safeName}{extension.ToLowerInvariant()}";
        var fullPath = Path.Combine(_mediaRoot, storedFileName);

        await using (var stream = File.Create(fullPath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        lock (_lock)
        {
            var entry = new MediaLibraryEntry(
                id,
                string.IsNullOrWhiteSpace(file.FileName) ? storedFileName : file.FileName,
                storedFileName,
                mediaKind,
                file.Length,
                DateTimeOffset.UtcNow);

            _entries.RemoveAll(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            _entries.Add(entry);
            PersistUnsafe();
            return ToSnapshot(entry);
        }
    }

    public bool DeleteAsset(string id)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return false;
            }

            var fullPath = Path.Combine(_mediaRoot, entry.StoredFileName);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            _entries.RemoveAll(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            PersistUnsafe();
            return true;
        }
    }

    public bool TryResolveAsset(string id, out string fullPath, out string mediaKind, out string fileName)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                fullPath = string.Empty;
                mediaKind = "none";
                fileName = string.Empty;
                return false;
            }

            fullPath = Path.Combine(_mediaRoot, entry.StoredFileName);
            mediaKind = entry.MediaKind;
            fileName = entry.DisplayName;
            return File.Exists(fullPath);
        }
    }

    private List<MediaLibraryEntry> LoadManifest()
    {
        try
        {
            if (!File.Exists(_manifestPath))
            {
                return [];
            }

            var json = File.ReadAllText(_manifestPath);
            return JsonSerializer.Deserialize<List<MediaLibraryEntry>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load media library manifest.");
            return [];
        }
    }

    private void PersistUnsafe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
            File.WriteAllText(_manifestPath, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist media library manifest.");
        }
    }

    private void PruneMissingFilesUnsafe()
    {
        var before = _entries.Count;
        _entries = _entries
            .Where(entry => File.Exists(Path.Combine(_mediaRoot, entry.StoredFileName)))
            .ToList();

        if (_entries.Count != before)
        {
            PersistUnsafe();
        }
    }

    private static MediaAssetSnapshot ToSnapshot(MediaLibraryEntry entry)
    {
        var url = $"/api/media/local/{Uri.EscapeDataString(entry.Id)}";
        return new MediaAssetSnapshot(
            entry.Id,
            entry.DisplayName,
            entry.MediaKind,
            url,
            url,
            entry.SizeBytes,
            entry.AddedAt);
    }

    private static string DetectMediaKind(string extension, string? contentType)
    {
        if (ImageExtensions.Contains(extension) || (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "image";
        }

        if (VideoExtensions.Contains(extension) || (contentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "video";
        }

        return "none";
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeChars = fileName
            .Where(ch => !invalid.Contains(ch))
            .Select(ch => char.IsWhiteSpace(ch) ? '-' : ch)
            .ToArray();
        var safe = new string(safeChars).Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "asset" : safe;
    }

    private sealed record MediaLibraryEntry(
        string Id,
        string DisplayName,
        string StoredFileName,
        string MediaKind,
        long SizeBytes,
        DateTimeOffset AddedAt);
}
