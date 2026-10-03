using UniversalMediaOS.Core.OtherMedia.Books;

namespace UniversalMediaOS.WPF.ViewModels;

public sealed class NavigateToBookDetailsMessage
{
    public NavigateToBookDetailsMessage(BookRecord book)
    {
        Book = book ?? throw new ArgumentNullException(nameof(book));
    }

    public BookRecord Book { get; }
}

public sealed class NavigateToBookReaderMessage
{
    public NavigateToBookReaderMessage(BookRecord book, BookAsset asset)
    {
        Book = book ?? throw new ArgumentNullException(nameof(book));
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
    }

    public BookRecord Book { get; }
    public BookAsset Asset { get; }
}
