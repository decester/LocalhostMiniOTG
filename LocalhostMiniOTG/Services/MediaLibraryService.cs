using System.Text.Json;

namespace LocalhostMiniOTG.Services;

public class MediaLibraryService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".ogv", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v"
    };

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".cr2", ".nef", ".arw", ".dng"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wma", ".flac", ".aac", ".ogg", ".wav", ".m4a"
    };

    public static bool IsPhoto(string ext) => PhotoExtensions.Contains(ext);
    public static bool IsAudio(string ext) => AudioExtensions.Contains(ext);
    public static bool IsVideo(string ext) => VideoExtensions.Contains(ext);

    private readonly string _settingsPath;
    private readonly string _favoritesPath;
    private string _mediaRoot = "";

    public MediaLibraryService(IWebHostEnvironment env)
    {
        _settingsPath = Path.Combine(env.ContentRootPath, "media-settings.json");
        _favoritesPath = Path.Combine(env.ContentRootPath, "favorites.json");
        Load(env.ContentRootPath);
    }

    public string MediaRoot
    {
        get => _mediaRoot;
        set
        {
            _mediaRoot = value;
            Save();
        }
    }

    public bool HasRoot => !string.IsNullOrEmpty(_mediaRoot) && Directory.Exists(_mediaRoot);

    public List<VideoFileInfo> GetVideos(string? subfolder = null)
    {
        var dir = ResolvePath(subfolder);
        if (dir == null || !Directory.Exists(dir))
            return [];

        return Directory.EnumerateFiles(dir)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(f => new VideoFileInfo
            {
                FileName = Path.GetFileName(f),
                RelativePath = GetRelativePath(f),
                DisplayName = Path.GetFileNameWithoutExtension(f),
                SizeBytes = new FileInfo(f).Length
            })
            .ToList();
    }

    public List<MediaFileInfo> GetPhotos(string? subfolder = null)
    {
        var dir = ResolvePath(subfolder);
        if (dir == null || !Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir)
            .Where(f => PhotoExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(f => new MediaFileInfo
            {
                FileName = Path.GetFileName(f),
                RelativePath = GetRelativePath(f),
                DisplayName = Path.GetFileNameWithoutExtension(f),
                SizeBytes = new FileInfo(f).Length
            })
            .ToList();
    }

    public List<MediaFileInfo> GetAudios(string? subfolder = null)
    {
        var dir = ResolvePath(subfolder);
        if (dir == null || !Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(f => new MediaFileInfo
            {
                FileName = Path.GetFileName(f),
                RelativePath = GetRelativePath(f),
                DisplayName = Path.GetFileNameWithoutExtension(f),
                SizeBytes = new FileInfo(f).Length
            })
            .ToList();
    }

    public string? ResolveFullPath(string relativePath)
    {
        if (!HasRoot) return null;
        var full = Path.GetFullPath(Path.Combine(_mediaRoot, relativePath));
        if (!full.StartsWith(Path.GetFullPath(_mediaRoot), StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    public List<FolderInfo> GetSubfolders(string? subfolder = null)
    {
        var dir = ResolvePath(subfolder);
        if (dir == null || !Directory.Exists(dir))
            return [];

        return Directory.EnumerateDirectories(dir)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(d => new FolderInfo
            {
                Name = Path.GetFileName(d)!,
                RelativePath = Path.GetRelativePath(_mediaRoot, d).Replace('\\', '/')
            })
            .ToList();
    }

    public string? ResolveVideoFullPath(string relativePath)
    {
        if (!HasRoot) return null;
        var full = Path.GetFullPath(Path.Combine(_mediaRoot, relativePath));
        if (!full.StartsWith(Path.GetFullPath(_mediaRoot), StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    public static List<DriveEntry> GetDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DriveEntry
            {
                Name = d.Name,
                Label = string.IsNullOrEmpty(d.VolumeLabel) ? d.Name : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})",
                TotalGB = Math.Round(d.TotalSize / 1_073_741_824.0, 1),
                FreeGB = Math.Round(d.AvailableFreeSpace / 1_073_741_824.0, 1)
            })
            .ToList();
    }

    public static List<BrowseEntry> BrowseFolder(string path)
    {
        if (!Directory.Exists(path))
            return [];

        var entries = new List<BrowseEntry>();

        var parent = Directory.GetParent(path);
        if (parent != null)
        {
            entries.Add(new BrowseEntry
            {
                Name = "..",
                FullPath = parent.FullName.Replace('\\', '/')
            });
        }

        foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(d => Path.GetFileName(d)))
        {
            try
            {
                var files = Directory.EnumerateFiles(dir).Select(f => Path.GetExtension(f)).ToList();
                entries.Add(new BrowseEntry
                {
                    Name = Path.GetFileName(dir)!,
                    FullPath = dir.Replace('\\', '/'),
                    VideoCount = files.Count(f => VideoExtensions.Contains(f)),
                    PhotoCount = files.Count(f => PhotoExtensions.Contains(f)),
                    AudioCount = files.Count(f => AudioExtensions.Contains(f))
                });
            }
            catch (UnauthorizedAccessException) { }
        }

        return entries;
    }

    private string? ResolvePath(string? subfolder)
    {
        if (!HasRoot) return null;
        if (string.IsNullOrEmpty(subfolder)) return _mediaRoot;

        var full = Path.GetFullPath(Path.Combine(_mediaRoot, subfolder));
        if (!full.StartsWith(Path.GetFullPath(_mediaRoot), StringComparison.OrdinalIgnoreCase))
            return null;
        return full;
    }

    private string GetRelativePath(string fullPath)
    {
        return Path.GetRelativePath(_mediaRoot, fullPath).Replace('\\', '/');
    }

    private void Load(string contentRoot)
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<Settings>(json);
                if (settings != null && Directory.Exists(settings.MediaRoot))
                {
                    _mediaRoot = settings.MediaRoot;
                    return;
                }
            }
            catch { }
        }
        _mediaRoot = Path.Combine(contentRoot, "Movies");
        if (!Directory.Exists(_mediaRoot))
            Directory.CreateDirectory(_mediaRoot);
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(new Settings { MediaRoot = _mediaRoot });
        File.WriteAllText(_settingsPath, json);
    }

    // --- Favorites ---

    public List<FavoriteFolder> GetFavorites()
    {
        if (!File.Exists(_favoritesPath)) return [];
        try
        {
            var json = File.ReadAllText(_favoritesPath);
            return JsonSerializer.Deserialize<List<FavoriteFolder>>(json) ?? [];
        }
        catch { return []; }
    }

    public void AddFavorite(string relativePath, string? label = null)
    {
        var favs = GetFavorites();
        if (favs.Any(f => f.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase)))
            return;
        favs.Add(new FavoriteFolder
        {
            Path = relativePath,
            Label = label ?? Path.GetFileName(relativePath.TrimEnd('/')) ?? relativePath
        });
        File.WriteAllText(_favoritesPath, JsonSerializer.Serialize(favs));
    }

    public void RemoveFavorite(string relativePath)
    {
        var favs = GetFavorites();
        favs.RemoveAll(f => f.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(_favoritesPath, JsonSerializer.Serialize(favs));
    }

    private class Settings
    {
        public string MediaRoot { get; set; } = "";
    }
}

public class VideoFileInfo
{
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long SizeBytes { get; set; }

    public string FormattedSize
    {
        get
        {
            double size = SizeBytes;
            string[] units = ["B", "KB", "MB", "GB"];
            int i = 0;
            while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
            return $"{size:0.##} {units[i]}";
        }
    }
}

public class FolderInfo
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
}

public class DriveEntry
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public double TotalGB { get; set; }
    public double FreeGB { get; set; }
}

public class MediaFileInfo
{
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string FormattedSize
    {
        get
        {
            double size = SizeBytes;
            string[] units = ["B", "KB", "MB", "GB"];
            int i = 0;
            while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
            return $"{size:0.##} {units[i]}";
        }
    }
}

public class BrowseEntry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public int VideoCount { get; set; }
    public int PhotoCount { get; set; }
    public int AudioCount { get; set; }
}

public class FavoriteFolder
{
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
}
