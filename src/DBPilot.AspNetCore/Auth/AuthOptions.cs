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

    /// <summary>PBKDF2 哈希（pbkdf2$iterations$salt$hash）；默认值 = 默认密码 dbpilot@2026 的哈希（部署后应修改，见 README「修改登录密码」）。</summary>
    public string PasswordHash { get; set; } = "pbkdf2$100000$IOaBWezPYRnzEbBWYwLxZA==$xBWm8jjDfanDmyRttfnZoG4MIk8lcF0UnDbpjJuWaEU=";

    public int ExpiresHours { get; set; } = 12;

    public string CookieName { get; set; } = "dbpilot_auth";

    /// <summary>
    /// 主密钥（Cookie 签名 + 实例凭据 AES-GCM）；为空时回退环境变量 DBPILOT_MASTER_KEY，
    /// 仍为空启动即报错（重启后已录入实例密码将无法解密，不做随机密钥兜底）。
    /// </summary>
    public string? Secret { get; set; }
}
