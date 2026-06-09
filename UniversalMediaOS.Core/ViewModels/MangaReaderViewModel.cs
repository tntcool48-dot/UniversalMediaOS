using System.Collections.Generic;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.ViewModels
{
    public class MangaReaderViewModel
    {
        public List<string> CurrentChapterImageUrls { get; private set; } = new List<string>();

        public async Task LoadChapterAsync(string mangaId, string chapterId)
        {
            // Stub for Consumet Manga API
            await Task.Delay(100);
            CurrentChapterImageUrls.Add("https://example.com/page1.jpg");
        }
    }
}
