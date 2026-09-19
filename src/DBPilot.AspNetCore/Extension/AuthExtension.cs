using DBPilot.Core.Auth;
using DBPilot.Core.Crypto;
using DBPilot.AspNetCore.Auth;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DBPilot.AspNetCore.Extension;

/// <summary>
/// 认证/加密模块（密钥共享链整域不拆散）：
/// 最简登录认证（单一账号 + 签名 Cookie）+ 实例凭据 AES-GCM 加密，
/// 主密钥回退链：DBPilot:Auth:Secret → DBPILOT_MASTER_KEY 环境变量；均缺失启动即报错
/// （主密钥同时签名 Cookie 与加密实例凭据，随机密钥会让重启后已录入实例密码全部无法解密，宁可拒启）。
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
            throw new InvalidOperationException(
                "DBPilot 主密钥未配置：请在 appsettings.json 设置 DBPilot:Auth:Secret，或设置环境变量 DBPILOT_MASTER_KEY（≥32 字符随机串）。"
                + "主密钥同时用于登录 Cookie 签名与实例凭据 AES-GCM 加密，缺失会导致重启后已录入实例的密码无法解密，故启动直接失败。");
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
