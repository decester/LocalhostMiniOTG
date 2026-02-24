# ?? LocalhostMiniOTG

A **zero-config LAN media server** — stream your videos, photos, and music from your PC to any device on your network. Just run the `.exe`, point it at a folder, and open the URL on your phone.

Built for the simplest possible "I want to watch my files on my iPhone" experience.

![.NET 10](https://img.shields.io/badge/.NET-10-purple)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![License](https://img.shields.io/badge/license-MIT-green)

---

## ? Features

### ?? Video Streaming
- **Direct play** — MP4, MOV, M4V, WebM stream natively to any browser
- **HLS for MKV/AVI/WMV** — automatically transcodes piece-by-piece into HLS segments so iPhone Safari can play them natively (no app needed)
- **GPU-accelerated** — auto-detects NVIDIA NVENC, Intel QuickSync, or AMD AMF
- **Instant seek** — full seek bar from the start; seeking ahead restarts FFmpeg at that position
- **Range requests** — proper `206 Partial Content` support for all devices

### ?? Photo Viewer
- **Formats** — JPG, PNG, GIF, BMP, WebP
- **RAW support** — CR2 (Canon), NEF (Nikon), ARW (Sony), DNG — auto-converted to JPEG via FFmpeg
- **Thumbnail grid** — small 300px JPEG thumbnails generated on the fly and cached in RAM
- **Gallery navigation** — ? ? buttons to browse through photos

### ?? Audio Player
- **Formats** — MP3, FLAC, WMA, AAC, OGG, WAV, M4A
- Native browser audio player with full controls

### ?? Dedicated Music Player (`/Music`)
- **Separate tab** — music keeps playing while you browse other content
- **iPhone lock screen controls** — track title, prev/next via Media Session API
- **Background playback** — lock your iPhone, music keeps playing
- **Playlist mode** — auto-plays next track, shuffle, repeat
- **Prev / Stop / Next / Shuffle / Repeat** controls
- **Spinning disc** animation while playing
- **? Favorite folders** — bookmark your music folders for one-tap access
- **"Open Music Player"** button on the main page opens it in a new tab

### ?? File Browser
- Built-in folder picker — browse drives and folders from the web UI
- Breadcrumb navigation for subfolders
- Media counts shown per folder (videos, photos, audio)
- ? Save favorite folder locations (persisted to `favorites.json`)

### ?? Zero Config
- **Single `.exe`** — no install, no dependencies (self-contained .NET 10)
- **Auto-opens browser** on startup
- **Auto-configures Windows Firewall** for LAN access
- **Remembers your media folder** between sessions
- Shows LAN IP in console — just share the URL

---

## ?? Quick Start

### 1. Download

Grab `LocalhostMiniOTG.exe` from [Releases](../../releases).

### 2. (Optional) Add FFmpeg

Only needed for MKV/AVI playback on iPhone and RAW photo support.

1. Download from [gyan.dev/ffmpeg/builds](https://www.gyan.dev/ffmpeg/builds/) ? **ffmpeg-release-essentials.zip**
2. Extract ? find `bin\ffmpeg.exe` and `bin\ffprobe.exe`
3. Copy both into the **same folder** as `LocalhostMiniOTG.exe`

### 3. Run

```
LocalhostMiniOTG.exe
```

You'll see:

```
===========================================
  ?? LocalhostMiniOTG is running!
  Local:   http://localhost:9090
  Network: http://192.168.1.50:9090
  Firewall: rule auto-configured ?
  FFmpeg:  found ?
  GPU:     NVIDIA NVENC (RTX/GTX) ?
  Press Ctrl+C to stop the server.
===========================================
```

### 4. Open on your phone

Open `http://192.168.1.50:9090` (your Network URL) in Safari or Chrome.

---

## ?? iPhone Compatibility

| Format | iPhone Safari | How |
|--------|:---:|---|
| MP4, MOV, M4V | ? | Direct streaming |
| MKV, AVI, WMV, FLV | ? | HLS transcoding (needs FFmpeg) |
| JPG, PNG, GIF, WebP | ? | Direct |
| CR2, NEF, ARW, DNG | ? | Auto-converted to JPEG (needs FFmpeg) |
| MP3, AAC, WAV, M4A | ? | Direct |
| FLAC, OGG | ? | Native browser support |
| WMA | ?? | Limited browser support |

### ?? iPhone Music Experience

Play music on your iPhone — lock the screen — **it keeps playing.** Full lock screen controls work:

```
???????????????????????????????????
?  ?? iPhone Lock Screen          ?
?                                 ?
?    ?? My Favorite Song          ?
?       LocalhostMiniOTG          ?
?       Music                     ?
?                                 ?
?    ??    ?    ??                ?
?    ??????????????? 2:34 / 4:12 ?
???????????????????????????????????
```

**How:** Open `/Music` in Safari ? play a track ? lock phone ? enjoy ??

This works because the dedicated Music page uses the [Media Session API](https://developer.mozilla.org/en-US/docs/Web/API/Media_Session_API), which tells iOS to show track info and playback controls on the lock screen and Control Center.

---

## ??? Architecture

```
LocalhostMiniOTG.exe
??? Razor Pages UI (dark theme, responsive)
??? /Music            ? dedicated music player (lock screen controls)
??? /api/stream       ? serves any file with range request support
??? /api/photo/thumb  ? generates & caches 300px JPEG thumbnails
??? /api/photo        ? converts RAW ? JPEG via FFmpeg
??? /api/hls/start    ? starts HLS transcoding session
??? /api/hls/{id}/*   ? serves m3u8 playlist & .ts segments
??? /api/favorites    ? save/load favorite folder bookmarks
??? /api/browse/*     ? drive & folder browser
```

### HLS Streaming (MKV ? iPhone)

```
MKV file ? ffprobe (get duration) ? generate full VOD playlist
         ? FFmpeg splits into 4-sec .ts segments
         ? segments cached in RAM, files deleted
         ? iPhone requests segments on demand
         ? seek ahead? FFmpeg restarts with -ss at that position
```

---

## ??? Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
# Clone
git clone https://github.com/YOUR_USERNAME/LocalhostMiniOTG.git
cd LocalhostMiniOTG

# Run in development
dotnet run --project LocalhostMiniOTG

# Publish single-file exe
dotnet publish LocalhostMiniOTG\LocalhostMiniOTG.csproj -c Release -o publish
```

---

## ?? Project Structure

```
LocalhostMiniOTG/
??? Program.cs                      # API endpoints & server setup
??? Services/
?   ??? MediaLibraryService.cs      # File listing, folder browsing, favorites
?   ??? HlsStreamManager.cs        # HLS transcoding, GPU detection, segment caching
??? Pages/
?   ??? Index.cshtml                # Main UI (player, gallery, browser)
?   ??? Index.cshtml.cs             # Page model
?   ??? Music.cshtml                # Dedicated music player (playlist, lock screen)
?   ??? Music.cshtml.cs             # Music page model
?   ??? Shared/_Layout.cshtml       # Dark theme layout with nav tabs
??? wwwroot/css/site.css            # Custom styles
```

---

## ?? Configuration

| Setting | Default | How to change |
|---|---|---|
| Port | `9090` | `--urls http://0.0.0.0:8080` |
| Media folder | `Movies/` subfolder | Change in web UI |
| FFmpeg location | Auto-detected | Place next to `.exe` or in `PATH` |

---

## ?? Credits

- [FFmpeg](https://ffmpeg.org/) — video/audio transcoding & RAW photo conversion
- [hls.js](https://github.com/video-dev/hls.js/) — HLS playback in Chrome/Firefox
- [Bootstrap 5](https://getbootstrap.com/) — responsive dark UI
- [Bootstrap Icons](https://icons.getbootstrap.com/) — iconography

---

## ?? License

MIT — do whatever you want with it.
