using System.Security.Cryptography;
using DBPilot.Core.Auth;
using DBPilot.Core.Crypto;
using DBPilot.AspNetCore.Auth;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace DBPilot.AspNetCore.Extension;

/// <summary>
/// 认证/加密模块（密钥共享链整域不拆散）：
/// 最简登录认证（单一账号 + 签名 Cookie）+ 实例凭据 AES-GCM 加密，
/// 主密钥回退链：DBPilot:Auth:Secret → DBPILOT_MASTER_KEY 环境变量 → 随机密钥（重启后登录会话失效，告警）。
/// </summary>
public static class AuthExtension
{
    /// <summary>
    /// 注册登录认证 + 实例凭据 AES-GCM（主密钥回退链见类注释）。
    /// 幂等（TryAdd）：Web 必带登录（AddDbpilotWeb 内含本方法），Collector 解密凭据也依赖——
    /// 全家桶无条件调用后再进 AddDbpilotWeb 不会重复注册。
    /// </summary>
    public static WebApplicationBuilder AddDbpilotAuth(this WebApplicationBuilder builder)
    {
        var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        authOptions.Secret ??= Environment.GetEnvironmentVariable("DBPILOT_MASTER_KEY");
        if (string.IsNullOrEmpty(authOptions.Secret))
        {
            authOptions.Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            Log.Warning("DBPilot:Auth:Secret 未配置且环境变量 DBPILOT_MASTER_KEY 缺失，已使用随机密钥（重启后所有登录会话失效）");
        }
        builder.Services.TryAddSingleton(authOptions);
        builder.Services.TryAddSingleton(new AuthTicket(authOptions.Secret!));

        // 实例凭据加密：主密钥与登录签名共用（同一回退链）
        builder.Services.TryAddSingleton(new AesGcmCrypto(authOptions.Secret!));

        return builder;
    }

    /// <summary>/api/** 认证拦截（/api/login、/api/health 放行）；须在 MapControllers 之前。</summary>
    public static WebApplication UseDbpilotAuth(this WebApplication app)
    {
        app.UseSimpleAuth();
        return app;
    }
}
