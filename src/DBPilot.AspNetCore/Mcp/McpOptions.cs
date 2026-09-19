using DBPilot.AspNetCore.Extension;
namespace DBPilot.AspNetCore.Mcp;

/// <summary>
/// MCP 出口配置（appsettings DBPilot:Mcp 节）。
/// 无独立开关——ApiKey 非空即视为开启（安全默认关：ApiKey 缺省为空）。
/// </summary>
public class McpOptions
{
    public const string SectionName = DbpilotConfigKeys.Mcp;

    /// <summary>API key（请求头 X-Api-Key 必须精确匹配；为空时模块不注册）。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>列表类工具返回的 SQL 文本头部长度（沿 DX 证据口径；agent 需要全文再调 fulltext 工具）。</summary>
    public int MaxSqlHeadLength { get; set; } = 500;

    /// <summary>列表类工具单次返回行数上限。</summary>
    public int MaxRows { get; set; } = 50;
}
