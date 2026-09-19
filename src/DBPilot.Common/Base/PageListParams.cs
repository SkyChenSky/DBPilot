using Newtonsoft.Json;

namespace DBPilot.Common;

/// <summary>
/// 分页请求参数：Page 默认 1，Limit 默认 10；setter 夹取防御非法值。
/// </summary>
public class PageListParams
{
    private int? _page;

    /// <summary>页码（从 1 开始）。</summary>
    public int Page
    {
        get => _page ?? 1;
        set => _page = value < 1 ? 1 : value;
    }

    private int? _limit;

    /// <summary>页长（1 ~ 200）。</summary>
    public int Limit
    {
        get => _limit ?? 10;
        set => _limit = value < 1 ? 1 : (value > 200 ? 200 : value);
    }
}

/// <summary>带附加查询参数的分页请求。</summary>
public class PageListParams<TParam> : PageListParams where TParam : new()
{
    public PageListParams()
    {
        Params = new TParam();
    }

    /// <summary>搜索参数。</summary>
    [JsonIgnore]
    public TParam Params { get; set; }
}
