using Chloe;
using DBPilot.Common;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using Mapster;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DBPilot.Core.PerformanceInsight;

/// <summary>
/// 分钟聚合落库服务（SampleFlushJob 每 60s 调用）：
/// 环形缓冲里上一完整分钟 → MinuteAggregator 聚合 → dbpilot_active_request_sample
/// （幂等：单事务先删该分钟再插；MarkFlushed 防重复处理；内存缓冲语义下重启丢一分钟可接受）。
/// </summary>
public class SampleFlushService(IServiceProvider sp, SampleBufferRegistry registry, IPlatformDialect dialect) : IDepend
{
    static SampleFlushService()
    {
        // MinuteAggregate → 实体：异名列 + JSON 计算列（Mapster 全局配置，MapTo 使用）
        TypeAdapterConfig<MinuteAggregate, DbpilotActiveRequestSample>.NewConfig()
            .Map(d => d.MinuteTime, s => s.MinuteUtc)
            .Map(d => d.AvgActiveSessions, s => s.AvgActive)
            .Map(d => d.MaxActiveSessions, s => s.MaxActive)
            // JSON 列统一 SerializeExtension 口径（CamelCase + UTC ISO；桶/dim/指纹键本身均小写，不受影响；
            // 读侧 ParseBuckets/ParseDims 反序列化字典键 verbatim，存量数据兼容）
            .Map(d => d.Buckets, s => s.Buckets.ToJson())
            .Map(d => d.Dims, s => s.Dims.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(x => new { key = x.Key, value = x.Value }).ToList()).ToJson())
            .Map(d => d.CreateTime, _ => DateTime.UtcNow)
            .Ignore(d => d.Id);
    }

    /// <summary>主落库入口：各实例缓冲中上一完整分钟聚合写库，并顺带落库未持久化的 SQL 模板。</summary>
    public async Task FlushAsync()
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return;

        var minute = DateTime.UtcNow.AddMinutes(-1);
        minute = minute.AddTicks(-(minute.Ticks % TimeSpan.TicksPerMinute));   // 截整分

        foreach (var instanceId in registry.InstanceIds)
        {
            InstanceSampleBuffer? buffer = null;
            var marked = false;
            try
            {
                buffer = registry.Of(instanceId);
                await PersistSqlTemplatesAsync(db, dialect, instanceId, buffer);

                var ticks = buffer.Minute(minute);
                if (ticks.Count == 0 || !(marked = buffer.MarkFlushedIfNew(minute))) continue;

                var agg = MinuteAggregator.Aggregate(minute, ticks);
                if (agg == null) continue;

                var entity = ToEntity(instanceId, agg);
                // 单事务：Delete 提交后 Insert 失败会把该分钟净丢（下轮只处理上一分钟不补）——合事务后原子
                db.Session.BeginTransaction();
                try
                {
                    await db.DeleteAsync<DbpilotActiveRequestSample>(
                        x => x.InstanceId == instanceId && x.MinuteTime == minute);
                    await db.InsertAsync(entity);
                    db.Session.CommitTransaction();
                }
                catch
                {
                    db.Session.RollbackTransaction();
                    throw;
                }
            }
            catch (Exception ex)
            {
                // 落库失败回退分钟标记（下轮重试；Delete+Insert 本身幂等）
                if (marked) buffer?.UnmarkFlushed(minute);
                Log.Error(ex, "实例 {Id} 分钟聚合落库失败（{Minute:u}）", instanceId, minute);
            }
        }
    }

    /// <summary>聚合结果 → 实体（列映射见静态构造的 TypeAdapterConfig；InstanceId 由调用方补充）。</summary>
    internal static DbpilotActiveRequestSample ToEntity(int instanceId, MinuteAggregate agg)
    {
        var entity = agg.MapTo<MinuteAggregate, DbpilotActiveRequestSample>();
        entity.InstanceId = instanceId;
        return entity;
    }

    /// <summary>SQL 模板字典落库（dbpilot_sql_template，UNIQUE(instance_id, fingerprint)）：只插未持久化的新指纹
    /// （预查仅减无效往返；并发安全由方言原子 upsert 保证——与 TopSQL/慢SQL Job 并发撞键实测）。</summary>
    private static async Task PersistSqlTemplatesAsync(DbContext db, IPlatformDialect dialect, int instanceId, InstanceSampleBuffer buffer)
    {
        var pending = buffer.SqlTemplates.Values.Where(t => !t.Persisted).ToList();
        foreach (var t in pending)
        {
            try
            {
                db.SqlQuery<int>(dialect.SqlTemplateUpsertSql(), new
                {
                    instanceId,
                    fingerprint = t.Fingerprint,
                    sqlText = t.SqlText,
                    firstSeen = t.FirstSeenUtc,
                    lastSeen = DateTime.UtcNow,
                });
                t.Persisted = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "实例 {Id} SQL 模板落库失败（{Fingerprint}）", instanceId, t.Fingerprint);
            }
        }
    }
}
