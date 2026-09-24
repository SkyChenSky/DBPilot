using Quartz;
using Serilog;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>
/// Job 基类：统一异常兜底（单次失败只记日志，不炸调度器）；
/// 业务服务直接构造注入——Quartz ≥3.3.2 默认 JobFactory 每次执行自建 DI scope 解析 Job，
/// Scoped 服务（Chloe 上下文等）生命周期安全且执行完即释放。
/// </summary>
public abstract class DbpilotJob : IJob
{
    /// <summary>本次执行的 Quartz 上下文（MergedJobDataMap 等；手动触发传参走这里）。</summary>
    protected IJobExecutionContext? Context { get; private set; }

    public async Task Execute(IJobExecutionContext context)
    {
        Context = context;
        try
        {
            await RunAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // 关停中，正常退出
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Job} 执行失败", GetType().Name);
        }
    }

    protected abstract Task RunAsync(CancellationToken ct);
}
