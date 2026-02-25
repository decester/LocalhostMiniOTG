using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LocalhostMiniOTG.Services;

/// <summary>
/// Manages HLS streaming sessions. FFmpeg splits MKV into small .ts segments
/// that iPhone Safari can play natively. Segments are kept in RAM and cleaned up.
/// </summary>
public class HlsStreamManager : IDisposable
{
    private readonly ConcurrentDictionary<string, HlsSession> _sessions = new();
    private readonly Timer _cleanupTimer;

    private static readonly HashSet<string> NeedsHls = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".avi", ".wmv", ".flv", ".ogv"
    };

    private static string? _ffmpegPath;
    private static bool _searched;
    private static string? _cachedVideoEnc;
    private static string? _cachedGpuName;

    /// <summary>Video quality presets for different network speeds.</summary>
    public enum VideoQuality { Auto, Low, Medium, High }

    /// <summary>
    /// Returns FFmpeg args for the given quality level.
    /// Low  = 480p  500kbps 2s segments (slow WiFi / far away)
    /// Med  = 720p  1.5Mbps 2s segments (normal WiFi)
    /// High = original quality 4s segments (fast LAN)
    /// </summary>
    public static (string videoArgs, string scaleFilter, string audioArgs, int segmentDuration) GetQualityArgs(VideoQuality quality)
    {
        var baseEnc = GetVideoEncoderArgs();

        // NOTE: In FFmpeg filters, comma is the filter separator.
        // Inside expressions like if(gt(a,b),c,d) the comma must be escaped as \, on Windows.
        return quality switch
        {
            VideoQuality.Low => (
                AddBitrateCap(baseEnc, "500k", "700k", "200k"),
                "-vf \"scale=if(gt(iw\\,854)\\,854\\,trunc(iw/2)*2):trunc(ow/a/2)*2\"",
                "-c:a aac -b:a 64k -ac 2",
                2
            ),
            VideoQuality.Medium => (
                AddBitrateCap(baseEnc, "1500k", "2000k", "500k"),
                "-vf \"scale=if(gt(iw\\,1280)\\,1280\\,trunc(iw/2)*2):trunc(ow/a/2)*2\"",
                "-c:a aac -b:a 96k -ac 2",
                2
            ),
            _ => (
                baseEnc,
                "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\"",
                "-c:a aac -b:a 192k -ac 2",
                4
            )
        };
    }

    private static string AddBitrateCap(string encoderArgs, string bitrate, string maxrate, string bufsize)
    {
        // bufsize controls how strictly the bitrate is enforced per segment.
        // Smaller bufsize = smoother network usage = less spiking.
        if (encoderArgs.Contains("libx264"))
            return $"-c:v libx264 -preset ultrafast -b:v {bitrate} -maxrate {maxrate} -bufsize {bufsize} -pix_fmt yuv420p";
        if (encoderArgs.Contains("h264_nvenc"))
            return $"-c:v h264_nvenc -preset medium -b:v {bitrate} -maxrate {maxrate} -bufsize {bufsize} -pix_fmt yuv420p";
        if (encoderArgs.Contains("h264_qsv"))
            return $"-c:v h264_qsv -preset fast -b:v {bitrate} -maxrate {maxrate} -bufsize {bufsize} -pix_fmt yuv420p";
        if (encoderArgs.Contains("h264_amf"))
            return $"-c:v h264_amf -quality balanced -b:v {bitrate} -maxrate {maxrate} -bufsize {bufsize} -pix_fmt yuv420p";
        return encoderArgs;
    }

    public HlsStreamManager()
    {
        _cleanupTimer = new Timer(_ => CleanupIdle(), null,
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    public static bool NeedsHlsStream(string filePath)
        => NeedsHls.Contains(Path.GetExtension(filePath));

    public static bool IsFfmpegAvailable() => FindFfmpeg() != null;

    public static string GetGpuStatus()
    {
        DetectGpuEncoder(); // ensure detected
        return _cachedGpuName ?? "not detected";
    }

    /// <summary>
    /// Returns the FFmpeg video encoder args. Cached after first call.
    /// Tries NVENC ? QSV ? AMF ? CPU fallback.
    /// </summary>
    public static string GetVideoEncoderArgs()
    {
        if (_cachedVideoEnc != null) return _cachedVideoEnc;
        DetectGpuEncoder();
        return _cachedVideoEnc!;
    }

    private static void DetectGpuEncoder()
    {
        if (_cachedVideoEnc != null) return;

        var ff = FindFfmpeg();
        if (ff == null)
        {
            _cachedVideoEnc = "-c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p";
            _cachedGpuName = "CPU (libx264 ultrafast) — no FFmpeg";
            return;
        }

        // Print FFmpeg version for diagnostics
        try
        {
            using var vp = new Process();
            vp.StartInfo = new ProcessStartInfo { FileName = ff, Arguments = "-version", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            vp.Start();
            var ver = vp.StandardOutput.ReadLine();
            vp.WaitForExit(3000);
            Console.WriteLine($"  [GPU] FFmpeg: {ver}");
        }
        catch { }

        // Test each GPU encoder
        var encoders = new[]
        {
            (name: "NVIDIA NVENC (GTX/RTX)", encoder: "h264_nvenc",
             args: "-c:v h264_nvenc -preset medium -cq 22 -pix_fmt yuv420p"),
            (name: "Intel QuickSync", encoder: "h264_qsv",
             args: "-c:v h264_qsv -preset fast -global_quality 22 -pix_fmt yuv420p"),
            (name: "AMD AMF", encoder: "h264_amf",
             args: "-c:v h264_amf -quality balanced -qp_i 22 -qp_p 22 -pix_fmt yuv420p"),
        };

        foreach (var (name, encoder, args) in encoders)
        {
            Console.WriteLine($"  [GPU] Testing {name} ({encoder})...");
            var (ok, err) = TestEncoder(ff, encoder);
            if (ok)
            {
                Console.WriteLine($"  [GPU] ? {name} — will use GPU encoding");
                _cachedVideoEnc = args;
                _cachedGpuName = $"{name} ?";
                return;
            }
            else
            {
                Console.WriteLine($"  [GPU] ? {name} — {err}");
            }
        }

        Console.WriteLine("  [GPU] No GPU encoder found, using CPU (libx264 ultrafast)");

        // Check if NVIDIA GPU exists but driver is too old
        try
        {
            using var smi = new Process();
            smi.StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,driver_version --format=csv,noheader",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            smi.Start();
            var gpuInfo = smi.StandardOutput.ReadToEnd().Trim();
            smi.WaitForExit(5000);
            if (!string.IsNullOrEmpty(gpuInfo))
            {
                Console.WriteLine($"  [GPU] NVIDIA GPU detected: {gpuInfo}");
                Console.WriteLine("  [GPU] NVENC failed — your NVIDIA driver may be too old for this FFmpeg build.");
                Console.WriteLine("  [GPU] Try updating NVIDIA drivers to latest version from https://www.nvidia.com/drivers");
            }
        }
        catch { }

        _cachedVideoEnc = "-c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p";
        _cachedGpuName = "None — CPU (libx264 ultrafast)";
    }

    private static (bool ok, string error) TestEncoder(string ffmpegPath, string encoder)
    {
        // Try multiple test approaches — different FFmpeg versions and GPU generations need different params
        var testCmds = new[]
        {
            // Test 1: standard lavfi color source
            $"-hide_banner -y -f lavfi -i color=c=black:s=320x240:r=25 -pix_fmt yuv420p -t 0.2 -c:v {encoder} -frames:v 3",
            // Test 2: baseline profile + nv12 (most compatible with older GPUs like GTX 9xx Maxwell)
            $"-hide_banner -y -f lavfi -i color=c=black:s=320x240:r=25 -pix_fmt nv12 -t 0.2 -c:v {encoder} -profile:v baseline -frames:v 3",
            // Test 3: with -hwaccel auto
            $"-hide_banner -y -hwaccel auto -f lavfi -i color=c=black:s=320x240:r=25 -pix_fmt yuv420p -t 0.2 -c:v {encoder} -frames:v 3",
        };


        string lastErr = "unknown error";
        foreach (var baseArgs in testCmds)
        {
            string? tempFile = null;
            try
            {
                tempFile = Path.Combine(Path.GetTempPath(), $"miniOTG_gpu_test_{Guid.NewGuid():N}.ts");
                var testArgs = $"{baseArgs} \"{tempFile}\"";

                using var p = new Process();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = testArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                p.Start();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);

                if (p.ExitCode == 0 && File.Exists(tempFile) && new FileInfo(tempFile).Length > 0)
                    return (true, "");

                // Dump full stderr for debugging (first test only)
                if (testCmds[0] == baseArgs && stderr.Length > 0)
                    Console.WriteLine($"  [GPU]   stderr: {stderr.Trim().Replace("\n", " | ")}");

                var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                lastErr = lines.LastOrDefault(l =>
                    l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("Cannot", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("No such", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("Terminating", StringComparison.OrdinalIgnoreCase))
                    ?? lines.LastOrDefault() ?? "unknown error";
                lastErr = lastErr.Trim();
            }
            catch (Exception ex)
            {
                lastErr = ex.Message;
            }
            finally
            {
                if (tempFile != null)
                    try { File.Delete(tempFile); } catch { }
            }
        }

        return (false, lastErr);
    }

    public static string? FindFfmpeg()
    {
        if (_searched) return _ffmpegPath;
        _searched = true;

        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, "ffmpeg.exe"),
            Path.Combine(appDir, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(appDir, "ffmpeg", "bin", "ffmpeg.exe"),
        };

        foreach (var path in candidates)
            if (File.Exists(path)) { _ffmpegPath = path; return path; }

        // Check PATH
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg", Arguments = "-version",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            });
            p?.WaitForExit(3000);
            if (p?.ExitCode == 0) { _ffmpegPath = "ffmpeg"; return "ffmpeg"; }
        }
        catch { }

        _ffmpegPath = null;
        return null;
    }

    private readonly object _sessionLock = new();

    /// <summary>
    /// Start or get an existing HLS session. Only one session runs at a time —
    /// starting a new video automatically kills the previous FFmpeg process.
    /// </summary>
    public HlsSession GetOrStartSession(string relativeFile, string fullPath, HlsStreamManager.VideoQuality quality = HlsStreamManager.VideoQuality.Auto)
    {
        lock (_sessionLock)
        {
            var key = relativeFile.ToLowerInvariant();

            // If a session for the same file and quality already exists, return it
            if (_sessions.TryGetValue(key, out var existing) && existing.Quality == quality && existing.Error == null)
                return existing;

            // Kill all other sessions first (only one video at a time)
            foreach (var (oldKey, oldSession) in _sessions)
            {
                if (_sessions.TryRemove(oldKey, out var removed))
                    removed.Dispose();
            }

            var session = new HlsSession(relativeFile, fullPath, quality);
            _sessions[key] = session;
            return session;
        }
    }

    public void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
            session.Dispose();
    }

    public HlsSession? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var s) ? s : null;
    }

    private void CleanupIdle()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-15);
        foreach (var (key, session) in _sessions)
        {
            if (session.LastAccess < cutoff)
            {
                if (_sessions.TryRemove(key, out var removed))
                    removed.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var (_, session) in _sessions)
            session.Dispose();
        _sessions.Clear();
    }
}

/// <summary>
/// A single HLS transcoding session. Probes total duration first, generates a complete
/// VOD playlist (= full seek bar immediately). When user seeks ahead of what's encoded,
/// FFmpeg is restarted with -ss to jump to that position.
/// </summary>
public class HlsSession : IDisposable
{
    private const int SeekAheadThreshold = 5; // restart FFmpeg if seeking this many segments ahead

    private readonly int _segmentDuration;
    private readonly string _tempDir;
    private readonly string _fullPath;
    private readonly ConcurrentDictionary<string, byte[]> _segments = new();
    private readonly string? _staticPlaylist;
    private readonly object _ffmpegLock = new();
    private FileSystemWatcher? _watcher;
    private Process? _ffmpeg;
    private volatile int _highestSegment = -1;
    private volatile bool _complete;
    private bool _disposed;

    public string SessionId { get; }
    public string RelativeFile { get; }
    public DateTime LastAccess { get; private set; } = DateTime.UtcNow;
    public bool IsReady => _staticPlaylist != null || _segments.Count > 0;
    public bool IsComplete => _complete;
    public string? Error { get; private set; }
    public double DurationSeconds { get; private set; }
    public HlsStreamManager.VideoQuality Quality { get; }

    public HlsSession(string relativeFile, string fullPath, HlsStreamManager.VideoQuality quality = HlsStreamManager.VideoQuality.Auto)
    {
        RelativeFile = relativeFile;
        SessionId = relativeFile.ToLowerInvariant();
        Quality = quality;
        _fullPath = fullPath;

        // Get segment duration from quality preset
        var (_, _, _, segDur) = HlsStreamManager.GetQualityArgs(quality);
        _segmentDuration = segDur;

        _tempDir = Path.Combine(Path.GetTempPath(), $"miniOTG_hls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var ff = HlsStreamManager.FindFfmpeg();
        if (ff == null) { Error = "FFmpeg not found"; return; }

        // Probe total duration ? generate full VOD playlist ? full seek bar
        DurationSeconds = ProbeDuration(ff, fullPath);
        if (DurationSeconds > 0)
            _staticPlaylist = BuildVodPlaylist(DurationSeconds);

        // Start encoding from the beginning
        StartFfmpeg(ff, 0);
    }

    private void StartFfmpeg(string ff, int startSegment)
    {
        lock (_ffmpegLock)
        {
            if (_disposed) return;

            // Kill existing process and wait for it to fully exit
            KillFfmpeg();

            // Clear stale state
            _complete = false;
            Error = null;
            // Don't clear _segments — old cached segments are still valid for playback.
            // Only update _highestSegment to reflect where FFmpeg will start producing.
            _highestSegment = startSegment - 1;

            // Clean leftover .ts files from disk (already cached ones are in _segments)
            try
            {
                foreach (var f in Directory.GetFiles(_tempDir, "*.ts"))
                    try { File.Delete(f); } catch { }
                foreach (var f in Directory.GetFiles(_tempDir, "*.m3u8"))
                    try { File.Delete(f); } catch { }
            }
            catch { }

            var seekTime = startSegment * _segmentDuration;
            var segmentPattern = Path.Combine(_tempDir, "seg%04d.ts");
            var playlistPath = Path.Combine(_tempDir, "stream.m3u8");
            var (videoEnc, vf, audioArgs, _) = HlsStreamManager.GetQualityArgs(Quality);

            var seekArg = seekTime > 0 ? $"-ss {seekTime} " : "";

            var args =
                $"-y -hide_banner -loglevel error -hwaccel auto " +
                $"{seekArg}" +
                $"-i \"{_fullPath}\" " +
                "-map 0:v:0 -map 0:a:0? " +
                $"{vf} {videoEnc} " +
                $"{audioArgs} " +
                "-f hls " +
                $"-hls_time {_segmentDuration} " +
                "-hls_list_size 0 " +
                "-hls_flags independent_segments " +
                "-hls_segment_type mpegts " +
                $"-start_number {startSegment} " +
                $"-hls_segment_filename \"{segmentPattern}\" " +
                $"\"{playlistPath}\"";

            // Watch for new .ts files
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(_tempDir, "*.ts")
            {
                NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnSegmentCreated;

            _ffmpeg = new Process();
            _ffmpeg.StartInfo = new ProcessStartInfo
            {
                FileName = ff,
                Arguments = args,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _ffmpeg.EnableRaisingEvents = true;
            _ffmpeg.Exited += (_, _) =>
            {
                _complete = true;
                LoadAllSegments();
            };

            try
            {
                var proc = _ffmpeg;
                _ffmpeg.Start();
                Task.Run(async () =>
                {
                    try
                    {
                        var err = await proc.StandardError.ReadToEndAsync();
                        if (!proc.HasExited) proc.WaitForExit(5000);
                        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                            Error = err.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim();
                    }
                    catch { }
                });
            }
            catch (Exception ex) { Error = ex.Message; }
        }
    }

    private void KillFfmpeg()
    {
        if (_ffmpeg != null)
        {
            if (!_ffmpeg.HasExited)
            {
                try { _ffmpeg.Kill(true); } catch { }
                try { _ffmpeg.WaitForExit(3000); } catch { }
            }
            try { _ffmpeg.Dispose(); } catch { }
            _ffmpeg = null;
        }
    }

    /// <summary>
    /// Returns the pre-built VOD playlist (full seek bar).
    /// </summary>
    public string? GetPlaylist()
    {
        if (_disposed) return null;
        Touch();
        return _staticPlaylist;
    }

    /// <summary>
    /// Gets a segment. If it's far ahead of what's encoded, restarts FFmpeg with -ss.
    /// Waits up to 30s for the segment to be produced.
    /// </summary>
    public async Task<byte[]?> GetSegmentAsync(string name, CancellationToken cancel)
    {
        if (_disposed) return null;
        Touch();

        // Quick return if already cached
        if (_segments.TryGetValue(name, out var data))
            return data;

        // Parse segment index from name (seg0042.ts -> 42)
        var segIndex = ParseSegmentIndex(name);

        // If seeking far ahead of what FFmpeg has encoded, restart FFmpeg at that position
        if (segIndex >= 0 && segIndex > _highestSegment + SeekAheadThreshold && !_complete && !_disposed)
        {
            var ff = HlsStreamManager.FindFfmpeg();
            if (ff != null)
                StartFfmpeg(ff, segIndex);
        }

        // Check disk
        data = TryReadFromDisk(name);
        if (data != null) return data;

        // Wait for FFmpeg to produce this segment (up to 30s)
        for (int i = 0; i < 100; i++)
        {
            if (cancel.IsCancellationRequested || _disposed) break;

            try { await Task.Delay(300, cancel); }
            catch (OperationCanceledException) { break; }

            if (_segments.TryGetValue(name, out data))
                return data;

            data = TryReadFromDisk(name);
            if (data != null) return data;

            if (_complete) break;
        }

        return null;
    }

    private static int ParseSegmentIndex(string name)
    {
        // seg0042.ts ? 42
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.StartsWith("seg") && int.TryParse(stem[3..], out var idx))
            return idx;
        return -1;
    }

    private byte[]? TryReadFromDisk(string name)
    {
        if (_disposed) return null;
        var path = Path.Combine(_tempDir, name);
        try
        {
            if (!File.Exists(path)) return null;
            var data = File.ReadAllBytes(path);
            if (data.Length > 0)
            {
                _segments[name] = data;
                UpdateHighest(name);
                try { File.Delete(path); } catch { }
                return data;
            }
        }
        catch { }
        return null;
    }

    private void OnSegmentCreated(object sender, FileSystemEventArgs e)
    {
        Task.Delay(300).ContinueWith(_ => TryLoadSegment(e.FullPath));
    }

    private void TryLoadSegment(string path)
    {
        if (_disposed) return;
        var name = Path.GetFileName(path);
        if (_segments.ContainsKey(name)) return;
        for (int i = 0; i < 5; i++)
        {
            if (_disposed) return;
            try
            {
                if (!File.Exists(path)) return;
                var data = File.ReadAllBytes(path);
                if (data.Length > 0)
                {
                    _segments[name] = data;
                    UpdateHighest(name);
                    try { File.Delete(path); } catch { }
                    return;
                }
            }
            catch { }
            Thread.Sleep(100);
        }
    }

    private void UpdateHighest(string name)
    {
        var idx = ParseSegmentIndex(name);
        if (idx > _highestSegment)
            _highestSegment = idx;
    }

    private void LoadAllSegments()
    {
        if (_disposed) return;
        try
        {
            foreach (var file in Directory.GetFiles(_tempDir, "*.ts"))
                TryLoadSegment(file);
        }
        catch { }
    }

    private void Touch() => LastAccess = DateTime.UtcNow;

    // --- Probe & playlist ---

    private static double ProbeDuration(string ffmpegPath, string inputPath)
    {
        var ffprobe = ffmpegPath == "ffmpeg" ? "ffprobe"
            : Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe" + Path.GetExtension(ffmpegPath));
        if (ffprobe != "ffprobe" && !File.Exists(ffprobe))
            ffprobe = "ffprobe";

        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            p.Start();
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            if (double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur) && dur > 0)
                return dur;
        }
        catch { }
        return 0;
    }

    private string BuildVodPlaylist(double totalDuration)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        sb.AppendLine($"#EXT-X-TARGETDURATION:{_segmentDuration}");
        sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");

        int segIndex = 0;
        double remaining = totalDuration;
        while (remaining > 0.1)
        {
            var segDur = Math.Min(_segmentDuration, remaining);
            sb.AppendLine($"#EXTINF:{segDur:F6},");
            sb.AppendLine($"seg{segIndex:D4}.ts");
            segIndex++;
            remaining -= _segmentDuration;
        }

        sb.AppendLine("#EXT-X-ENDLIST");
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watcher?.Dispose();
        lock (_ffmpegLock) { KillFfmpeg(); }

        _segments.Clear();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }
}
