# 🎬 LocalhostMiniOTG

**Stream videos, photos, and music from your PC to your iPhone (or any device) over Wi-Fi.**

No apps to install. No cloud. No accounts. Just run the `.exe` and open the URL on your phone.

[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows)](https://github.com/decester/LocalhostMiniOTG)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 💡 What it does

You have files on your PC. You want to watch/listen/view them on your phone. That's it.

- 🎬 **Videos** play in the browser - even MKV and AVI on iPhone (auto-transcoded to HLS)
- 📷 **Photos** display with thumbnails and a gallery - even Canon CR2 RAW files
- 🎵 **Music** plays in a dedicated player - keeps playing when you lock your iPhone

---

## 🚀 Quick Start

### 1. 📥 Download

Grab `LocalhostMiniOTG.exe` from [Releases](https://github.com/decester/LocalhostMiniOTG/releases).

### 2. 🎬 (Optional) Add FFmpeg

Only needed for MKV/AVI streaming and RAW photo support.

Download from [gyan.dev/ffmpeg/builds](https://www.gyan.dev/ffmpeg/builds/) (get **essentials** build), extract `ffmpeg.exe` and `ffprobe.exe`, and place them next to `LocalhostMiniOTG.exe`.

### 3. ▶️ Run

```
LocalhostMiniOTG.exe
```

```
===========================================
  LocalhostMiniOTG is running!
  Local:   http://localhost:9090
  Network: http://192.168.1.50:9090
  Firewall: rule auto-configured
  FFmpeg:  found
  GPU:     NVIDIA NVENC (RTX/GTX)
  Press Ctrl+C to stop the server.
===========================================
```

### 4. 📱 Open on your phone

Type the **Network** URL into Safari or Chrome on your phone. Done.

---

## ✨ Features

### 🎬 Video Streaming

| What | How |
|---|---|
| MP4, MOV, M4V, WebM | Direct play - no conversion needed |
| MKV, AVI, WMV, FLV | Auto-transcoded to HLS (piece by piece, cached in RAM) |
| GPU acceleration | Auto-detects NVIDIA NVENC, Intel QuickSync, AMD AMF |
| Seeking | Full seek bar from the start - seeking ahead restarts FFmpeg at that position |
| Range requests | Proper `206 Partial Content` for all devices |

### 📸 Photo Viewer

| What | How |
|---|---|
| JPG, PNG, GIF, BMP, WebP | Direct display |
| CR2, NEF, ARW, DNG | RAW files auto-converted to JPEG via FFmpeg |
| Thumbnails | 300px JPEG thumbnails generated on the fly, cached in RAM |
| Gallery | Previous / Next navigation, photo counter |

### 🎧 Music Player (`/Music` page)

A dedicated music player that opens in its own browser tab.

| What | How |
|---|---|
| MP3, FLAC, AAC, OGG, WAV, M4A, WMA | Native browser audio with full controls |
| Background playback | Lock your iPhone - music keeps playing |
| Lock screen controls | Track title + prev/next buttons on iOS lock screen |
| Playlist | Auto-plays next track, shuffle, repeat |
| Favorite folders | Bookmark folders for one-tap access |

**🔥 This is the killer feature.** Open `/Music` on your iPhone, pick a folder, hit play, lock the phone - it just works. Track info shows on the lock screen with skip controls, powered by the [Media Session API](https://developer.mozilla.org/en-US/docs/Web/API/Media_Session_API).

```
+-----------------------------------+
|  iPhone Lock Screen               |
|                                   |
|     My Favorite Song              |
|     LocalhostMiniOTG              |
|                                   |
|     |<<      >>|     >>|          |
|     ========o========= 2:34/4:12 |
+-----------------------------------+
```

### 📁 File Browser and Favorites

- 📂 Built-in folder picker (browse drives and directories from the web UI)
- 🪧 Breadcrumb navigation
- 📊 Media counts per folder (videos, photos, audio)
- ⭐ Star button to save favorite folders (persisted across restarts)

### 🔧 Zero Config

- 📦 **Single `.exe`** - self-contained, no install, no runtime needed
- 🌐 **Auto-opens browser** on startup
- 🛡️ **Auto-configures Windows Firewall** rule for LAN access
- 💾 **Remembers** your media folder and favorites between sessions
- 📟 **Console shows LAN IP** - just share it

---

## 📱 iPhone Compatibility

| Format | Works | Method |
|---|:---:|---|
| MP4, MOV, M4V | ✅ | Direct streaming |
| MKV, AVI, WMV, FLV | ✅ | HLS transcoding (needs FFmpeg) |
| JPG, PNG, GIF, WebP | ✅ | Direct |
| CR2, NEF, ARW, DNG | ✅ | Converted to JPEG (needs FFmpeg) |
| MP3, AAC, WAV, M4A, FLAC, OGG | ✅ | Native browser playback |
| WMA | ⚠️ | Limited browser support |

---

## 🎞️ How HLS Streaming Works

When you play an MKV on iPhone, the server does not convert the whole file first:

```
1. ffprobe reads the total duration
2. A complete .m3u8 playlist is generated upfront (full seek bar)
3. FFmpeg starts encoding 4-second .ts segments
4. Segments are cached in RAM, temp files deleted
5. iPhone requests segments on demand
6. If you seek ahead, FFmpeg restarts with -ss at that position
```

GPU encoding (NVENC/QSV/AMF) is auto-detected and used when available. Falls back to `libx264 ultrafast` on CPU.

---

## 🏗️ Architecture

```
LocalhostMiniOTG.exe
|-- /              Main UI (video player, photo gallery, file browser)
|-- /Music         Dedicated music player with playlist and lock screen controls
|-- /api/stream    Serves any media file with byte-range support
|-- /api/photo/thumb  Generates and caches 300px JPEG thumbnails
|-- /api/photo     Converts RAW photos to JPEG via FFmpeg
|-- /api/hls/*     HLS session management and segment serving
|-- /api/favorites Favorite folder bookmarks (GET/POST/DELETE)
|-- /api/browse/*  Drive and folder browser
```

---

## 🛠️ Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/decester/LocalhostMiniOTG.git
cd LocalhostMiniOTG

# Run
dotnet run --project LocalhostMiniOTG

# Publish single-file exe
dotnet publish LocalhostMiniOTG/LocalhostMiniOTG.csproj -c Release -o publish
```

---

## 📂 Project Structure

```
LocalhostMiniOTG/
  Program.cs                        API endpoints, server setup, firewall config
  Services/
    MediaLibraryService.cs          File listing, folder browsing, favorites
    HlsStreamManager.cs            HLS transcoding, GPU detection, seek, segment cache
  Pages/
    Index.cshtml / .cs              Main page (video, photo, audio, file browser)
    Music.cshtml / .cs              Dedicated music player (playlist, lock screen)
    Shared/_Layout.cshtml           Dark theme layout, navigation
  wwwroot/css/site.css              Custom styles
```

---

## ⚙️ Configuration

| Setting | Default | Override |
|---|---|---|
| Port | `9090` | `--urls http://0.0.0.0:8080` |
| Media folder | `Movies/` | Change in web UI |
| FFmpeg | Auto-detected | Place next to `.exe` or add to `PATH` |
| Favorites | `favorites.json` | Managed via web UI |

---

## 🙏 Credits

- [FFmpeg](https://ffmpeg.org/) - transcoding, RAW conversion, duration probing
- [hls.js](https://github.com/video-dev/hls.js/) - HLS playback for Chrome/Firefox
- [Bootstrap 5](https://getbootstrap.com/) - dark responsive UI
- [Bootstrap Icons](https://icons.getbootstrap.com/) - icons

---

## 📄 License

[MIT](LICENSE) - use it however you want.