using System.Linq.Expressions;
using Chloe;
using DBPilot.Common;

namespace DBPilot.Storage;

/// <summary>
/// Chloe 查询扩展（蓝本 SF.Chloe.Extension.QueryExtension，适配 DBPilot.Common.PageList）。
/// </summary>
public static class QueryExtension
{
    /// <summary>分页查询（Count + TakePage）。</summary>
    public static PageList<T> PageList<T>(this IQuery<T> query, int pageIndex, int pageSize)
    {
        var count = query.Count();
        var items = query.TakePage(pageIndex, pageSize).ToList();
        return new PageList<T> { Total = count, Items = items, PageIndex = pageIndex, PageSize = pageSize };
    }

    /// <summary>分页查询 + 投影。</summary>
    public static PageList<TResult> PageList<T, TResult>(this IQuery<T> query, int pageIndex, int pageSize,
        Expression<Func<T, TResult>> selector)
    {
        var count = query.Count();
        var items = query.Select(selector).TakePage(pageIndex, pageSize).ToList();
        return new PageList<TResult> { Total = count, Items = items, PageIndex = pageIndex, PageSize = pageSize };
    }

    /// <summary>分页查询（异步）。</summary>
    public static async Task<PageList<T>> PageListAsync<T>(this IQuery<T> query, int pageIndex, int pageSize)
    {
        var count = await query.CountAsync();
        var items = await query.TakePage(pageIndex, pageSize).ToListAsync();
        return new PageList<T> { Total = count, Items = items, PageIndex = pageIndex, PageSize = pageSize };
    }

    /// <summary>分页查询 + 投影（异步）。</summary>
    public static async Task<PageList<TResult>> PageListAsync<T, TResult>(this IQuery<T> query, int pageIndex, int pageSize,
        Expression<Func<T, TResult>> selector)
    {
        var count = await query.CountAsync();
        var items = await query.Select(selector).TakePage(pageIndex, pageSize).ToListAsync();
        return new PageList<TResult> { Total = count, Items = items, PageIndex = pageIndex, PageSize = pageSize };
    }

    /// <summary>排序：isDesc = true 时倒序。</summary>
    public static IQuery<T> OrderBy<T, TKey>(this IQuery<T> query, Expression<Func<T, TKey>> keySelector, bool isDesc)
        => isDesc ? query.OrderByDesc(keySelector) : query.OrderBy(keySelector);
}
