namespace UniversalMediaOS.Core.OtherMedia.Books;

public interface IBookCatalogProvider
{
    BookCatalogSource Source { get; }

    Task<BookSearchPage> SearchAsync(
        string query,
        int offset = 0,
        int limit = 24,
        CancellationToken cancellationToken = default);

    Task<BookRecord?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default);
}
