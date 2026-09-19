namespace DBPilot.Common;

/// <summary>
/// 统一分页模型：{ total, items, pageIndex, pageSize }。
/// TotalPage/HasPrev/HasNext 为只读计算属性。
/// </summary>
public class PageList<T>
{
    public long Total { get; set; }

    public List<T> Items { get; set; } = [];

    public int PageIndex { get; set; }

    public int PageSize { get; set; }

    /// <summary>总页数（Total=0 时为 0）。</summary>
    public int TotalPage => PageSize > 0
        ? (int)((Total + PageSize - 1) / PageSize)
        : 0;

    /// <summary>是否有上一页。</summary>
    public bool HasPrev => PageIndex > 1;

    /// <summary>是否有下一页。</summary>
    public bool HasNext => PageIndex < TotalPage;

    public static PageList<T> Empty(int pageIndex = 1, int pageSize = 20)
        => new() { Total = 0, Items = [], PageIndex = pageIndex, PageSize = pageSize };

    public static PageList<T> Create(IEnumerable<T> items, long total, int pageIndex, int pageSize)
        => new() { Total = total, Items = [.. items], PageIndex = pageIndex, PageSize = pageSize };
}
