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
            var entry = new MediaLibraryEntry
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(file.FileName) ? storedFileName : file.FileName,
                StoredFileName = storedFileName,
                MediaKind = mediaKind,
                SizeBytes = file.Length,
                AddedAt = DateTimeOffset.UtcNow,
                IsLinked = false,
                SourcePath = string.Empty
            };

            _entries.RemoveAll(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            _entries.Add(entry);
            PersistUnsafe();
            return ToSnapshot(entry);
        }
    }

    public MediaAssetSnapshot RegisterLinkedFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new InvalidOperationException("Local file path is required.");
        }

        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourcePath.Trim().Trim('"')));
        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new InvalidOperationException("Use a full local file path.");
        }

        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException("Local file does not exist.");
        }

        var fileInfo = new FileInfo(fullPath);
        var mediaKind = DetectMediaKind(fileInfo.Extension, null);
        if (mediaKind == "none")
        {
            throw new InvalidOperationException("Only image and video files are supported.");
        }

        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(item =>
                item.IsLinked &&
                string.Equals(item.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.DisplayName = fileInfo.Name;
                existing.MediaKind = mediaKind;
                existing.SizeBytes = fileInfo.Length;
                existing.AddedAt = DateTimeOffset.UtcNow;
                PersistUnsafe();
                return ToSnapshot(existing);
            }

            var entry = new MediaLibraryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = fileInfo.Name,
                StoredFileName = string.Empty,
                MediaKind = mediaKind,
                SizeBytes = fileInfo.Length,
                AddedAt = DateTimeOffset.UtcNow,
                IsLinked = true,
                SourcePath = fullPath
            };

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

            var fullPath = ResolveFullPath(entry);
            if (!entry.IsLinked && File.Exists(fullPath))
            {
                File.SetAttributes(fullPath, FileAttributes.Normal);
                File.Delete(fullPath);
                if (File.Exists(fullPath))
                {
                    throw new InvalidOperationException("Saved media file could not be deleted from disk.");
                }
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

            fullPath = ResolveFullPath(entry);
            mediaKind = entry.MediaKind;
            fileName = entry.DisplayName;
            return File.Exists(fullPath);
        }
    }

    public bool HasAsset(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            return entry is not null && File.Exists(ResolveFullPath(entry));
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
            using var document = JsonDocument.Parse(json);
            return ParseEntries(document.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load media library manifest.");
            return [];
        }
    }

    private static List<MediaLibraryEntry> ParseEntries(JsonElement root)
    {
        return root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray()
                .SelectMany(ParseEntryNode)
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
                .ToList(),
            JsonValueKind.Object => ParseEntry(root) is { Id.Length: > 0 } entry ? [entry] : [],
            _ => []
        };
    }

    private static IEnumerable<MediaLibraryEntry> ParseEntryNode(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var nested in element.EnumerateArray())
            {
                foreach (var entry in ParseEntryNode(nested))
                {
                    yield return entry;
                }
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var entry = ParseEntry(element);
            if (!string.IsNullOrWhiteSpace(entry.Id))
            {
                yield return entry;
            }
        }
    }

    private static MediaLibraryEntry ParseEntry(JsonElement element)
    {
        return new MediaLibraryEntry
        {
            Id = GetString(element, "id"),
            DisplayName = GetString(element, "displayName"),
            StoredFileName = GetString(element, "storedFileName"),
            MediaKind = GetString(element, "mediaKind", "none"),
            SizeBytes = GetInt64(element, "sizeBytes"),
            AddedAt = GetDateTimeOffset(element, "addedAt"),
            IsLinked = GetBool(element, "isLinked"),
            SourcePath = GetString(element, "sourcePath")
        };
    }

    private static string GetString(JsonElement element, string propertyName, string fallback = "")
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;

    private static long GetInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : 0L;

    private static DateTimeOffset GetDateTimeOffset(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(property.GetString(), out var value)
            ? value
            : DateTimeOffset.UtcNow;

    private static bool GetBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : false;

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
            .Where(entry => File.Exists(ResolveFullPath(entry)))
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
            entry.AddedAt,
            entry.IsLinked);
    }

    private string ResolveFullPath(MediaLibraryEntry entry)
    {
        return entry.IsLinked
            ? entry.SourcePath
            : Path.Combine(_mediaRoot, entry.StoredFileName);
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

    private sealed class MediaLibraryEntry
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string StoredFileName { get; set; } = string.Empty;
        public string MediaKind { get; set; } = "none";
        public long SizeBytes { get; set; }
        public DateTimeOffset AddedAt { get; set; }
        public bool IsLinked { get; set; }
        public string SourcePath { get; set; } = string.Empty;
    }
}
