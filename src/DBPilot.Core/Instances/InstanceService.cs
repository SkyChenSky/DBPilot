using Chloe;
using DBPilot.Common;
using DBPilot.Core.Crypto;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.Core.Instances;

/// <summary>
/// 实例管理服务：CRUD + 凭据加密 + 创建/更新后 best-effort 探测刷新状态。
/// 平台库未配置（DBPilot:ConnectionString 为空）时相关方法返回失败，不抛异常。
/// </summary>
public class InstanceService(IServiceProvider sp, AesGcmCrypto crypto, IDatabaseProvider provider) : IDepend
{
    private static string DbNotConfigured => InstanceConfigResolver.DbNotConfiguredFor("管理实例");

    /// <summary>分页列表（keyword 匹配名称 / 主机）。</summary>
    public async Task<PageList<InstanceListItem>?> GetPageAsync(string? keyword, int page, int limit)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return null;

        var cond = ExpressionBuilder.Init<DbpilotInstance>();
        if (!keyword.IsNullOrEmpty())
            cond = cond.And(x => x.Name.Contains(keyword) || x.Host.Contains(keyword));

        var pageList = await db.Query<DbpilotInstance>()
            .Where(cond)
            .OrderByDesc(x => x.Id)
            .PageListAsync(page, limit);

        return PageList<InstanceListItem>.Create(
            pageList.Items.Select(InstanceListItem.From),
            pageList.Total, pageList.PageIndex, pageList.PageSize);
    }

    /// <summary>解密后的运行时配置（供 Provider 连接；严禁输出到日志/响应）。</summary>
    public async Task<InstanceConfig?> GetConfigAsync(int id)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return null;

        var e = await db.Query<DbpilotInstance>().FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return null;

        return new InstanceConfig
        {
            Id = e.Id,
            Name = e.Name,
            Host = e.Host,
            Port = e.Port,
            Engine = InstanceConfigResolver.EngineOrDefault(e.Engine),
            LoginName = e.LoginName,
            Password = crypto.Decrypt(e.PasswordCipher),
            Enabled = e.Enabled,
            SlowSqlThresholdMs = e.SlowSqlThresholdMs,
            BlockingThresholdSec = e.BlockingThresholdSec,
            CommandTimeoutSeconds = e.CommandTimeoutSeconds,
            XeFilePath = e.XeFilePath,
            DbFilter = e.DbFilter
        };
    }

    /// <summary>创建实例：校验 → 凭据加密入库 → best-effort 探测（失败不阻断创建）。</summary>
    public async Task<ServiceResult<InstanceListItem>> CreateAsync(InstanceSaveRequest request)
    {
        var validated = Validate(request, requirePassword: true);
        if (validated != null) return ServiceResult<InstanceListItem>.Failed(validated);

        var db = sp.GetService<DbContext>();
        if (db is null) return ServiceResult<InstanceListItem>.Failed(DbNotConfigured);

        if (await db.Query<DbpilotInstance>().AnyAsync(x => x.Name == request.Name))
            return ServiceResult<InstanceListItem>.Failed($"实例名称「{request.Name}」已存在");

        var entity = new DbpilotInstance
        {
            Name = request.Name!.Trim(),
            Host = request.Host!.Trim(),
            Port = request.Port ?? 1433,
            Engine = NormalizeEngine(request.Engine) ?? DbpilotEngines.SqlServer,
            LoginName = request.LoginName!.Trim(),
            PasswordCipher = crypto.Encrypt(request.Password!),
            Enabled = request.Enabled,
            EnvTag = request.EnvTag?.Trim(),
            SlowSqlThresholdMs = request.SlowSqlThresholdMs,
            BlockingThresholdSec = request.BlockingThresholdSec,
            CommandTimeoutSeconds = request.CommandTimeoutSeconds,
            XeFilePath = request.XeFilePath?.Trim(),
            DbFilter = request.DbFilter,
            CreateTime = DateTime.UtcNow
        };
        await db.InsertAsync(entity);   // Chloe 自动回填自增 Id

        await ProbeAndUpdateAsync(db, entity);
        return ServiceResult<InstanceListItem>.Succeeded(InstanceListItem.From(entity));
    }

    /// <summary>更新实例：Password 为空 = 保持原密码；同样 best-effort 探测。</summary>
    public async Task<ServiceResult<InstanceListItem>> UpdateAsync(int id, InstanceSaveRequest request)
    {
        var validated = Validate(request, requirePassword: false);
        if (validated != null) return ServiceResult<InstanceListItem>.Failed(validated);

        var db = sp.GetService<DbContext>();
        if (db is null) return ServiceResult<InstanceListItem>.Failed(DbNotConfigured);

        var entity = await db.Query<DbpilotInstance>().FirstOrDefaultAsync(x => x.Id == id);
        if (entity is null) return ServiceResult<InstanceListItem>.Failed("实例不存在");

        // 名称唯一性（排除自身）
        if (await db.Query<DbpilotInstance>().AnyAsync(x => x.Name == request.Name && x.Id != id))
            return ServiceResult<InstanceListItem>.Failed($"实例名称「{request.Name}」已存在");

        entity.Name = request.Name!.Trim();
        entity.Host = request.Host!.Trim();
        entity.Port = request.Port ?? 1433;

        // 更新时 engine 为空 = 保持不变（开关切换等局部更新不带引擎）
        var engine = NormalizeEngine(request.Engine);
        if (engine != null) entity.Engine = engine;

        entity.LoginName = request.LoginName!.Trim();
        if (!request.Password.IsNullOrEmpty())
            entity.PasswordCipher = crypto.Encrypt(request.Password!);
        entity.Enabled = request.Enabled;
        entity.EnvTag = request.EnvTag?.Trim();
        entity.SlowSqlThresholdMs = request.SlowSqlThresholdMs;
        entity.BlockingThresholdSec = request.BlockingThresholdSec;
        entity.CommandTimeoutSeconds = request.CommandTimeoutSeconds;
        entity.XeFilePath = request.XeFilePath?.Trim();
        entity.DbFilter = request.DbFilter;
        entity.UpdateTime = DateTime.UtcNow;

        await db.UpdateAsync(entity);

        await ProbeAndUpdateAsync(db, entity);
        return ServiceResult<InstanceListItem>.Succeeded(InstanceListItem.From(entity));
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return ServiceResult.Failed(DbNotConfigured);

        var affected = await db.DeleteAsync<DbpilotInstance>(x => x.Id == id);
        return affected > 0
            ? ServiceResult.Succeeded("已删除")
            : ServiceResult.Failed("实例不存在");
    }

    /// <summary>测试已保存实例（解密凭据），结果（含版本元数据刷新）写回 instance 表。引擎未注册时返回明确报错。</summary>
    public async Task<ServiceResult<ConnectionTestResult>> TestSavedAsync(int id)
    {
        var cfg = await GetConfigAsync(id);
        if (cfg is null) return ServiceResult<ConnectionTestResult>.Failed("实例不存在或平台库未配置");

        ConnectionTestResult test;
        try
        {
            test = await provider.TestConnectionAsync(cfg);
        }
        catch (DbpilotUnsupportedException ex)
        {
            return ServiceResult<ConnectionTestResult>.Failed(ex.Message);
        }

        var db = sp.GetService<DbContext>();
        var entity = db is null ? null : await db.Query<DbpilotInstance>().FirstOrDefaultAsync(x => x.Id == id);
        if (entity is not null)
            await ProbeAndUpdateAsync(db!, entity);

        return ServiceResult<ConnectionTestResult>.Succeeded(test);
    }

    /// <summary>测试未保存凭据（接入向导“测试连接”按钮，不落库）。引擎未注册时转失败结果（接入向导弹窗直显报错，不炸 500）。</summary>
    public async Task<ConnectionTestResult> TestUnsavedAsync(InstanceSaveRequest request)
    {
        try
        {
            return await provider.TestConnectionAsync(new InstanceConfig
            {
                Name = request.Name ?? string.Empty,
                Host = request.Host ?? string.Empty,
                Port = request.Port ?? 1433,
                Engine = NormalizeEngine(request.Engine) ?? DbpilotEngines.SqlServer,
                LoginName = request.LoginName ?? string.Empty,
                Password = request.Password ?? string.Empty
            });
        }
        catch (DbpilotUnsupportedException ex)
        {
            return new ConnectionTestResult { Ok = false, Error = ex.Message };
        }
    }

    /// <summary>库列表（索引诊断页下拉等）。</summary>
    public async Task<ServiceResult<List<string>>> GetDatabasesAsync(int id)
    {
        var cfg = await GetConfigAsync(id);
        if (cfg is null) return ServiceResult<List<string>>.Failed("实例不存在或平台库未配置");

        try
        {
            return ServiceResult<List<string>>.Succeeded(await provider.GetDatabasesAsync(cfg));
        }
        catch (DbpilotUnsupportedException ex)
        {
            // 引擎未注册（如只引了 SqlServer 包却接入 MySQL 实例）：原文透出，FriendlyMessage 会折叠成"服务器内部错误"
            return ServiceResult<List<string>>.Failed($"获取库列表失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return ServiceResult<List<string>>.Failed($"获取库列表失败：{ex.FriendlyMessage()}");
        }
    }

    /// <summary>best-effort 探测：连通则写版本元数据 + 在线状态；失败置离线并记 last_error。不抛异常。</summary>
    private async Task ProbeAndUpdateAsync(DbContext db, DbpilotInstance entity)
    {
        try
        {
            var cfg = new InstanceConfig
            {
                Host = entity.Host,
                Port = entity.Port,
                Engine = InstanceConfigResolver.EngineOrDefault(entity.Engine),
                LoginName = entity.LoginName,
                Password = crypto.Decrypt(entity.PasswordCipher)
            };

            var test = await provider.TestConnectionAsync(cfg);
            if (!test.Ok)
            {
                entity.Status = 3;   // 离线
                entity.LastError = test.Error?.Sub(500);
                entity.LastHeartbeat = DateTime.UtcNow;
            }
            else
            {
                var meta = await provider.ProbeAsync(cfg);
                entity.Status = 1;   // 在线
                entity.LastError = test.MissingPermissions.Count > 0
                    ? $"缺少权限：{string.Join("、", test.MissingPermissions.Select(p => p.Permission))}".Sub(500)
                    : null;
                entity.LastHeartbeat = DateTime.UtcNow;
                entity.ServerVersion = meta.ProductVersion;
                entity.MajorVersion = meta.MajorVersion;
                entity.Edition = meta.Edition;
                entity.CpuCores = meta.CpuCores;
                entity.MachineName = meta.MachineName;
                entity.SqlServerStartTime = meta.SqlServerStartTimeUtc;
                entity.ClockSkewSeconds = meta.ClockSkewSeconds;

                // XE 目录留空自动探测（慢SQL/2008 死锁路径 B 的文件目标目录）：探测默认日志目录回填并落库，
                // 手动填过的优先生效（仅覆盖空值）；MySQL 引擎 meta.DefaultLogPath 恒 null 天然跳过
                if (string.IsNullOrWhiteSpace(entity.XeFilePath) && !string.IsNullOrWhiteSpace(meta.DefaultLogPath))
                    entity.XeFilePath = meta.DefaultLogPath;
            }
        }
        catch (Exception ex)
        {
            entity.Status = 3;
            entity.LastError = ex.GetDeepestException().Message.Sub(500);
            entity.LastHeartbeat = DateTime.UtcNow;
        }

        entity.UpdateTime = DateTime.UtcNow;
        try
        {
            await db.UpdateAsync(entity);
        }
        catch
        {
            // best-effort：状态写回失败不影响主流程
        }
    }

    private static string? Validate(InstanceSaveRequest r, bool requirePassword)
    {
        if (r.Name.IsNullOrWhiteSpace()) return "实例名称不能为空";
        if (r.Name.Trim().Length > 100) return "实例名称过长（≤100 字符）";
        if (r.Host.IsNullOrWhiteSpace()) return "主机地址不能为空";
        if (r.Port is < 1 or > 65535) return "端口必须在 1~65535 之间";
        if (r.LoginName.IsNullOrWhiteSpace()) return "登录名不能为空";
        if (requirePassword && r.Password.IsNullOrEmpty()) return "密码不能为空";
        if (r.Password?.Length > 128) return "密码过长（≤128 字符）";
        if (r.SlowSqlThresholdMs is < 1 or > 600000) return "慢 SQL 阈值必须在 1~600000ms 之间";
        if (r.BlockingThresholdSec is < 1 or > 3600) return "阻塞判定阈值必须在 1~3600s 之间";
        if (r.CommandTimeoutSeconds is < 5 or > 86400) return "查询超时必须在 5~86400 秒之间";
        if (!r.Engine.IsNullOrWhiteSpace() && !DbpilotEngines.Known.Contains(NormalizeEngine(r.Engine)))
            return $"不支持的引擎类型「{r.Engine.Trim()}」";
        return null;
    }

    /// <summary>引擎标识归一（trim + 小写）；空/空白返回 null（创建=默认 sqlserver，更新=保持不变）。</summary>
    private static string? NormalizeEngine(string? engine)
    {
        var normalized = engine?.Trim().ToLowerInvariant();
        return normalized.IsNullOrEmpty() ? null : normalized;
    }
}
