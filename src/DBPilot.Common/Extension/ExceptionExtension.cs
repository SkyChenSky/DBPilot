using Microsoft.Data.SqlClient;
using Serilog;

namespace DBPilot.Common;

/// <summary>
/// 异常扩展方法（移植自 SF.Tookits.ExceptionExtension）。
/// </summary>
public static class ExceptionExtension
{
    /// <summary>获取最底层（最内层）异常。</summary>
    public static Exception GetDeepestException(this Exception ex)
    {
        var result = ex;
        var inner = ex.InnerException;
        while (inner is not null)
        {
            result = inner;
            inner = inner.InnerException;
        }

        return result;
    }

    /// <summary>
    /// 面向用户的友好错误文案：SqlException 按错误号归类（连接/超时/权限/锁冲突/查询失败），其余归"服务器内部错误"。
    /// 原始异常（SQL 片段/对象名/内网主机名）只进 Debug 日志不透出到浏览器。
    /// </summary>
    public static string FriendlyMessage(this Exception ex)
    {
        var deepest = ex.GetDeepestException();
        Log.Debug(deepest, "查询失败根因（仅日志）");

        return Classify(deepest is SqlException sql ? sql.Number : null);
    }

    /// <summary>SqlException 错误号 → 文案（单测覆盖；null = 非 SQL 异常）。</summary>
    internal static string Classify(int? number) => number switch
    {
        -1 or 2 or 53 or 10060 or 11001 => "连接被监控实例失败",
        -2 => "查询超时，请稍后重试",
        229 or 230 or 262 or 300 => "权限不足，请检查被监控账号权限",
        1205 => "查询发生死锁/锁冲突，请重试",
        not null => "数据库查询失败",
        _ => "服务器内部错误",
    };
}
