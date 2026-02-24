using LocalhostMiniOTG.Services;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

const int Port = 9090;
const string FirewallRuleName = "LocalhostMiniOTG Media Server";

var builder = WebApplication.CreateBuilder(args);

// Listen on all network interfaces so other devices on the LAN can connect
if (!args.Any(a => a.StartsWith("--urls")))
    builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");

builder.Services.AddRazorPages();
builder.Services.AddSingleton<MediaLibraryService>();
builder.Services.AddSingleton<HlsStreamManager>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// --- API: Browse drives ---
app.MapGet("/api/browse/drives", () => Results.Ok(MediaLibraryService.GetDrives()));

// --- API: Browse a folder on disk ---
app.MapGet("/api/browse/folder", (string path) =>
{
    if (!Directory.Exists(path))
        return Results.NotFound();
    return Results.Ok(MediaLibraryService.BrowseFolder(path));
});

// --- API: Set the media root folder ---
app.MapPost("/api/settings/media-root", (SetRootRequest req, MediaLibraryService lib) =>
{
    if (string.IsNullOrWhiteSpace(req.Path) || !Directory.Exists(req.Path))
        return Results.BadRequest("Folder does not exist.");
    lib.MediaRoot = req.Path;
    return Results.Ok(new { lib.MediaRoot });
});

// --- API: Get current media root ---
app.MapGet("/api/settings/media-root", (MediaLibraryService lib) =>
    Results.Ok(new { lib.MediaRoot, lib.HasRoot }));

// --- API: Favorite folders ---
app.MapGet("/api/favorites", (MediaLibraryService lib) => Results.Ok(lib.GetFavorites()));

app.MapPost("/api/favorites", (FavoriteReq req, MediaLibraryService lib) =>
{
    lib.AddFavorite(req.Path, req.Label);
    return Results.Ok(lib.GetFavorites());
});

app.MapDelete("/api/favorites", (string path, MediaLibraryService lib) =>
{
    lib.RemoveFavorite(path);
    return Results.Ok(lib.GetFavorites());
});

// --- API: Stream any media file (with range support for all devices) ---
app.MapGet("/api/stream", async (string file, HttpContext context, MediaLibraryService lib) =>
{
    var fullPath = lib.ResolveFullPath(file);
    if (fullPath == null) { context.Response.StatusCode = 404; return; }

    var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
    {
        // Video
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".m4v" => "video/x-m4v",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".webm" => "video/webm",
        ".ogv" => "video/ogg",
        ".wmv" => "video/x-ms-wmv",
        ".flv" => "video/x-flv",
        // Photo
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".cr2" or ".nef" or ".arw" or ".dng" => "image/jpeg", // RAW served via /api/photo/thumb
        // Audio
        ".mp3" => "audio/mpeg",
        ".wma" => "audio/x-ms-wma",
        ".flac" => "audio/flac",
        ".aac" => "audio/aac",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        _ => "application/octet-stream"
    };

    var fileLength = new FileInfo(fullPath).Length;
    var response = context.Response;
    response.Headers["Accept-Ranges"] = "bytes";
    response.ContentType = contentType;

    var rangeHeader = context.Request.Headers.Range.FirstOrDefault();
    if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
    {
        var range = rangeHeader["bytes=".Length..];
        var parts = range.Split('-');
        var start = long.TryParse(parts[0], out var s) ? s : 0;
        var end = parts.Length > 1 && long.TryParse(parts[1], out var e) ? e : fileLength - 1;

        if (start >= fileLength) { response.StatusCode = 416; return; }
        end = Math.Min(end, fileLength - 1);
        var chunkSize = end - start + 1;

        response.StatusCode = 206;
        response.ContentLength = chunkSize;
        response.Headers["Content-Range"] = $"bytes {start}-{end}/{fileLength}";

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var remaining = chunkSize;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), context.RequestAborted);
            if (read == 0) break;
            await response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            remaining -= read;
        }
    }
    else
    {
        response.StatusCode = 200;
        response.ContentLength = fileLength;
        await response.SendFileAsync(fullPath);
    }
});


// --- API: Photo thumbnail (small JPEG, cached in RAM) ---
var thumbCache = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();

app.MapGet("/api/photo/thumb", async (string file, HttpContext context, MediaLibraryService lib) =>
{
    var fullPath = lib.ResolveFullPath(file);
    if (fullPath == null) { context.Response.StatusCode = 404; return; }

    context.Response.ContentType = "image/jpeg";
    context.Response.Headers.CacheControl = "max-age=86400";

    // Return from cache if available
    if (thumbCache.TryGetValue(file, out var cached))
    {
        context.Response.ContentLength = cached.Length;
        await context.Response.Body.WriteAsync(cached, context.RequestAborted);
        return;
    }

    var ff = HlsStreamManager.FindFfmpeg();
    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
    var isRaw = ext is ".cr2" or ".nef" or ".arw" or ".dng";

    // For standard images without FFmpeg, resize isn't possible — serve original
    if (ff == null && !isRaw)
    {
        context.Response.Redirect($"/api/stream?file={Uri.EscapeDataString(file)}");
        return;
    }
    if (ff == null) { context.Response.StatusCode = 500; return; }

    try
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = $"-hide_banner -loglevel error -i \"{fullPath}\" -vf \"scale=300:-1\" -vframes 1 -f image2 -c:v mjpeg -q:v 4 pipe:1",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();

        using var ms = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(ms, context.RequestAborted);
        await process.WaitForExitAsync(context.RequestAborted);

        var bytes = ms.ToArray();
        if (bytes.Length > 0)
        {
            thumbCache[file] = bytes;
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
        else
        {
            context.Response.StatusCode = 500;
        }
    }
    catch (OperationCanceledException) { }
    catch { context.Response.StatusCode = 500; }
});

// --- API: Convert RAW photos (CR2/NEF/ARW/DNG) to JPEG via FFmpeg ---
app.MapGet("/api/photo", async (string file, HttpContext context, MediaLibraryService lib) =>
{
    var fullPath = lib.ResolveFullPath(file);
    if (fullPath == null) { context.Response.StatusCode = 404; return; }

    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
    var rawFormats = new HashSet<string> { ".cr2", ".nef", ".arw", ".dng" };

    if (!rawFormats.Contains(ext))
    {
        // Not RAW — redirect to normal stream
        context.Response.Redirect($"/api/stream?file={Uri.EscapeDataString(file)}");
        return;
    }

    // Convert RAW ? JPEG using FFmpeg
    var ff = HlsStreamManager.FindFfmpeg();
    if (ff == null) { context.Response.StatusCode = 500; return; }

    try
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ff,
            Arguments = $"-hide_banner -loglevel error -i \"{fullPath}\" -vframes 1 -f image2 -c:v mjpeg -q:v 2 pipe:1",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();

        context.Response.ContentType = "image/jpeg";
        context.Response.Headers.CacheControl = "max-age=3600";
        await process.StandardOutput.BaseStream.CopyToAsync(context.Response.Body, context.RequestAborted);
        await process.WaitForExitAsync(context.RequestAborted);
    }
    catch (OperationCanceledException) { }
    catch { context.Response.StatusCode = 500; }
});

// --- API: HLS streaming for MKV/AVI (piece-by-piece transcoding for iPhone) ---
app.MapPost("/api/hls/start", (HlsStartReq req, MediaLibraryService lib, HlsStreamManager hls) =>
{
    var fullPath = lib.ResolveVideoFullPath(req.File);
    if (fullPath == null) return Results.NotFound();
    if (!HlsStreamManager.NeedsHlsStream(fullPath)) return Results.BadRequest("Does not need HLS");
    if (!HlsStreamManager.IsFfmpegAvailable()) return Results.BadRequest("FFmpeg not found");

    var session = hls.GetOrStartSession(req.File, fullPath);
    return Results.Ok(new
    {
        sessionId = session.SessionId,
        ready = session.IsReady,
        complete = session.IsComplete,
        error = session.Error
    });
});

app.MapGet("/api/hls/status", (string id, HlsStreamManager hls) =>
{
    var session = hls.GetSession(id);
    if (session == null) return Results.NotFound();
    return Results.Ok(new
    {
        ready = session.IsReady,
        complete = session.IsComplete,
        error = session.Error
    });
});

app.MapGet("/api/hls/{id}/stream.m3u8", (string id, HttpContext context, HlsStreamManager hls) =>
{
    var session = hls.GetSession(id);
    var playlist = session?.GetPlaylist();
    if (playlist == null) return Results.NotFound();

    context.Response.Headers.CacheControl = "no-cache";
    return Results.Text(playlist, "application/vnd.apple.mpegurl");
});

app.MapGet("/api/hls/{id}/{segment}", async (string id, string segment, HttpContext context, HlsStreamManager hls) =>
{
    var session = hls.GetSession(id);
    if (session == null) return Results.NotFound();

    var data = await session.GetSegmentAsync(segment, context.RequestAborted);
    if (data == null) return Results.NotFound();

    context.Response.Headers.CacheControl = "max-age=3600";
    return Results.Bytes(data, "video/mp2t");
});

app.MapPost("/api/hls/stop", async (HttpContext context, HlsStreamManager hls) =>
{
    // sendBeacon sends text/plain, not JSON
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    try
    {
        var doc = System.Text.Json.JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetString();
        if (id != null) hls.StopSession(id);
    }
    catch { }
    return Results.Ok();
});

// Auto-open browser when running as standalone exe
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        FirewallHelper.EnsureRule(FirewallRuleName, Port);

    var localUrl = $"http://localhost:{Port}";
    var lanIp = Dns.GetHostEntry(Dns.GetHostName())
        .AddressList
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
    var lanUrl = lanIp != null ? $"http://{lanIp}:{Port}" : null;

    Console.WriteLine();
    Console.WriteLine("===========================================");
    Console.WriteLine("  ?? LocalhostMiniOTG is running!");
    Console.WriteLine($"  Local:   {localUrl}");
    if (lanUrl != null)
        Console.WriteLine($"  Network: {lanUrl}");
    Console.WriteLine("  Firewall: rule auto-configured ?");
    Console.WriteLine($"  FFmpeg:  {(HlsStreamManager.IsFfmpegAvailable() ? "found ?" : "not found (MKV won't play on iPhone)")}");
    if (HlsStreamManager.IsFfmpegAvailable())
    {
        Console.WriteLine($"  GPU:     {HlsStreamManager.GetGpuStatus()}");
    }
    Console.WriteLine("  Press Ctrl+C to stop the server.");
    Console.WriteLine("===========================================");
    Console.WriteLine();

    try { Process.Start(new ProcessStartInfo(localUrl) { UseShellExecute = true }); } catch { }
});

app.Run();

record SetRootRequest(string Path);
record HlsStartReq(string File);
record FavoriteReq(string Path, string? Label);

static class FirewallHelper
{
    public static void EnsureRule(string ruleName, int port)
    {
        try
        {
            var check = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"");
            if (check.Contains(ruleName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  [Firewall] Rule \"{ruleName}\" already exists.");
                return;
            }
        }
        catch { }

        try
        {
            var result = RunNetsh(
                $"advfirewall firewall add rule name=\"{ruleName}\" " +
                $"dir=in action=allow protocol=TCP localport={port} " +
                $"profile=private,public " +
                $"description=\"Allow LAN access to LocalhostMiniOTG media server on port {port}\"");

            if (result.Contains("Ok", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  [Firewall] Rule added for port {port} ?");
            else
                Console.WriteLine($"  [Firewall] Could not add rule. Run as Administrator if needed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Firewall] Failed: {ex.Message}");
            Console.WriteLine("  [Firewall] Try running the app as Administrator once.");
        }
    }

    private static string RunNetsh(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output;
    }
}
