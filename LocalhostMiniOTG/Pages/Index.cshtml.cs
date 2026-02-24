using LocalhostMiniOTG.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LocalhostMiniOTG.Pages
{
    public class IndexModel : PageModel
    {
        private readonly MediaLibraryService _library;

        public IndexModel(MediaLibraryService library)
        {
            _library = library;
        }

        public string MediaRoot => _library.MediaRoot;
        public bool HasRoot => _library.HasRoot;
        public List<VideoFileInfo> Videos { get; set; } = [];
        public List<MediaFileInfo> Photos { get; set; } = [];
        public List<MediaFileInfo> Audios { get; set; } = [];
        public List<FolderInfo> Subfolders { get; set; } = [];
        public string? CurrentFolder { get; set; }

        // Video player
        public string? NowPlaying { get; set; }
        public string? NowPlayingName { get; set; }
        public string? NowPlayingType { get; set; }
        public bool NowPlayingUseHls { get; set; }
        public bool FfmpegAvailable { get; set; }

        // Photo viewer
        public string? ViewingPhoto { get; set; }
        public string? ViewingPhotoName { get; set; }
        public int ViewingPhotoIndex { get; set; }

        // Audio player
        public string? PlayingAudio { get; set; }
        public string? PlayingAudioName { get; set; }
        public string? PlayingAudioType { get; set; }

        public void OnGet(string? folder, string? play, string? photo, string? audio)
        {
            CurrentFolder = folder;
            Subfolders = _library.GetSubfolders(folder);
            Videos = _library.GetVideos(folder);
            Photos = _library.GetPhotos(folder);
            Audios = _library.GetAudios(folder);
            FfmpegAvailable = HlsStreamManager.IsFfmpegAvailable();

            // Video
            if (!string.IsNullOrEmpty(play))
            {
                var fullPath = _library.ResolveVideoFullPath(play);
                if (fullPath != null)
                {
                    NowPlaying = play;
                    NowPlayingName = System.IO.Path.GetFileNameWithoutExtension(play);
                    NowPlayingUseHls = HlsStreamManager.NeedsHlsStream(fullPath) && FfmpegAvailable;
                    NowPlayingType = System.IO.Path.GetExtension(play).ToLowerInvariant() switch
                    {
                        ".mp4" => "video/mp4",
                        ".mov" => "video/quicktime",
                        ".m4v" => "video/x-m4v",
                        _ => "video/mp4"
                    };
                }
            }

            // Photo
            if (!string.IsNullOrEmpty(photo))
            {
                var fullPath = _library.ResolveFullPath(photo);
                if (fullPath != null)
                {
                    ViewingPhoto = photo;
                    ViewingPhotoName = System.IO.Path.GetFileNameWithoutExtension(photo);
                    ViewingPhotoIndex = Photos.FindIndex(p => p.RelativePath == photo);
                }
            }

            // Audio
            if (!string.IsNullOrEmpty(audio))
            {
                var fullPath = _library.ResolveFullPath(audio);
                if (fullPath != null)
                {
                    PlayingAudio = audio;
                    PlayingAudioName = System.IO.Path.GetFileNameWithoutExtension(audio);
                    PlayingAudioType = System.IO.Path.GetExtension(audio).ToLowerInvariant() switch
                    {
                        ".mp3" => "audio/mpeg",
                        ".flac" => "audio/flac",
                        ".aac" => "audio/aac",
                        ".ogg" => "audio/ogg",
                        ".wav" => "audio/wav",
                        ".m4a" => "audio/mp4",
                        _ => "audio/mpeg"
                    };
                }
            }
        }
    }
}
