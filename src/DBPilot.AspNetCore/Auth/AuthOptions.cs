using DBPilot.AspNetCore.Extension;
namespace DBPilot.AspNetCore.Auth;

/// <summary>
/// 最简登录认证配置（appsettings "DBPilot:Auth" 节）。
/// 单一固定账号；密码为 PBKDF2 哈希（生成：dotnet run --project samples/DBPilot.Sample.SqlServer -- --hash &lt;密码&gt;）。
/// </summary>
public class AuthOptions
{
    public const string SectionName = DbpilotConfigKeys.Auth;

    public string Username { get; set; } = "admin";

    /// <summary>PBKDF2 哈希（pbkdf2$iterations$salt$hash）</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public int ExpiresHours { get; set; } = 12;

    public string CookieName { get; set; } = "dbpilot_auth";

    /// <summary>
    /// Cookie 签名密钥；为空时回退环境变量 DBPILOT_MASTER_KEY，
    /// 仍为空则每次启动随机生成（重启即全员掉线，仅建议开发环境）。
    /// </summary>
    public string? Secret { get; set; }
}
