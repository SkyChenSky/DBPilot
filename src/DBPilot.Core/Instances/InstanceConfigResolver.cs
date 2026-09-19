using Chloe;
using DBPilot.Common;
using DBPilot.Core.Crypto;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.Core.Instances;

/// <summary>
/// 实例实体加载 + 解密为连接配置（各诊断服务共用）。
/// </summary>
public static class InstanceConfigResolver
{
    public const string DbNotConfigured = "平台库未配置（DBPilot:ConnectionString 为空），无法诊断";

    /// <summary>各服务场景化变体（诊断/管理实例/查询死锁/查询历史阻塞…），前缀与常量逐字一致。</summary>
    public static string DbNotConfiguredFor(string action)
        => $"平台库未配置（DBPilot:ConnectionString 为空），无法{action}";

    /// <summary>engine 空/空白 = sqlserver（dbpilot_instance.engine 口径；写入侧已归一 trim 小写）。</summary>
    internal static string EngineOrDefault(string? engine)
        => string.IsNullOrWhiteSpace(engine) ? DbpilotEngines.SqlServer : engine;

    public static async Task<(string? Error, DbpilotInstance? Entity)> LoadAsync(IServiceProvider sp, int id)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return (DbNotConfigured, null);

        var entity = await db.Query<DbpilotInstance>().FirstOrDefaultAsync(x => x.Id == id);
        return entity is null ? ("实例不存在", null) : (null, entity);
    }

    /// <summary>
    /// 查询侧引擎守卫：这些查询只读平台库、不触 Provider（死锁/计划/缺失索引），
    /// 不支持的引擎会静默返回空数据而非报错，故在服务层按 instance.engine 主动判定。
    /// 判定走 ProviderRegistry.Supports（能力矩阵由引擎包经 [DbpilotEngine].UnsupportedFeatures 自声明，
    /// 新增引擎 Core 零改动）；支持返回 null，不支持返回「功能不支持 XX 实例」文案；实例不存在返回提示。
    /// </summary>
    public static async Task<string?> RejectUnsupportedAsync(
        DbContext db, ProviderRegistry registry, int instanceId, string feature)
    {
        var entity = await db.Query<DbpilotInstance>().Where(x => x.Id == instanceId).FirstOrDefaultAsync();
        if (entity is null) return "实例不存在";

        var engine = EngineOrDefault(entity.Engine);
        return registry.Supports(engine, feature)
            ? null
            : DbpilotUnsupportedException.Feature(engine, feature).Message;
    }

    public static InstanceConfig ToConfig(AesGcmCrypto crypto, DbpilotInstance e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Host = e.Host,
        Port = e.Port,
        Engine = EngineOrDefault(e.Engine),
        LoginName = e.LoginName,
        Password = crypto.Decrypt(e.PasswordCipher),
        Enabled = e.Enabled,
        MajorVersion = e.MajorVersion ?? 0,
        SlowSqlThresholdMs = e.SlowSqlThresholdMs,
        BlockingThresholdSec = e.BlockingThresholdSec,
        XeFilePath = e.XeFilePath,
        DbFilter = e.DbFilter
    };
}
