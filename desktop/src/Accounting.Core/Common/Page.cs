namespace Accounting.Core.Common;

/// <summary>One page of a list, same shape as the Django API (count, page, page_size, total_pages, results).</summary>
public sealed record Page<T>(IReadOnlyList<T> Results, int Count, int PageNumber, int PageSize)
{
    public int TotalPages => Count == 0 ? 1 : (Count + PageSize - 1) / PageSize;

    /// <summary>Only 25, 50 and 100 rows per page are allowed; anything else falls back to 25.</summary>
    public static int NormalisePageSize(int? size) => size is 25 or 50 or 100 ? size.Value : 25;
}

/// <param name="All">Every row on one page (the online copy for phones); never set from a request.</param>
public sealed record PageRequest(int Page = 1, int? PageSize = null, bool All = false)
{
    public int Size => All ? 1_000_000 : Page<object>.NormalisePageSize(PageSize);
    public int Offset => (Math.Max(Page, 1) - 1) * Size;

    public static readonly PageRequest First = new();
}
