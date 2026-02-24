using LocalhostMiniOTG.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LocalhostMiniOTG.Pages
{
    public class MusicModel : PageModel
    {
        private readonly MediaLibraryService _library;

        public MusicModel(MediaLibraryService library)
        {
            _library = library;
        }

        public string MediaRoot => _library.MediaRoot;
        public bool HasRoot => _library.HasRoot;
        public List<MediaFileInfo> Tracks { get; set; } = [];
        public List<FolderInfo> Subfolders { get; set; } = [];
        public List<FavoriteFolder> Favorites { get; set; } = [];
        public string? CurrentFolder { get; set; }
        public int StartIndex { get; set; }

        public void OnGet(string? folder, int? track)
        {
            CurrentFolder = folder;
            Subfolders = _library.GetSubfolders(folder);
            Tracks = _library.GetAudios(folder);
            Favorites = _library.GetFavorites();
            StartIndex = track ?? 0;
        }
    }
}
