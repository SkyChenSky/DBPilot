using DBPilot.Common;

namespace DBPilot.Core.Providers;

/// <summary>
/// 监控账号无法访问任何目标库（缺库内用户映射等）：逐库采集"假成功"的防线。
/// 采集服务在全部目标库被逐库跳过时抛出（部分库可访问仍正常返回，不抛）；
/// API 侧文案直出（含修复指引），不并入「服务器内部错误」——与 <see cref="DbpilotUnsupportedException"/> 同一处理模式。
/// </summary>
public class DbpilotInaccessibleDbsException : Exception
{
    public DbpilotInaccessibleDbsException(string message) : base(message) { }

    /// <summary>标准文案：功能名 + 库名清单 + 修复脚本指引（库名截断防超长）。
    /// 原因措辞覆盖两类实测根因：库内无用户映射（登录进不了库）、有映射但缺 VIEW DEFINITION /
    /// VIEW DATABASE STATE（元数据/碎片查询被拒）——两条修复脚本一并给出。</summary>
    public static DbpilotInaccessibleDbsException Create(string label, IReadOnlyList<string> skipped)
        => new(
            $"{label}采集：目标库全部不可访问（{string.Join("、", skipped).Sub(300)}）。"
            + "账号无这些库的用户映射或库内权限不足，请在各库执行：USE [库名]; CREATE USER [账号] FOR LOGIN [账号]; "
            + "GRANT VIEW DEFINITION TO [账号]; GRANT VIEW DATABASE STATE TO [账号];");
}
