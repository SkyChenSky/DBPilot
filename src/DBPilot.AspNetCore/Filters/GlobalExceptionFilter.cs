using DBPilot.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DBPilot.AspNetCore.Filters;

/// <summary>
/// 全局异常过滤器：记录最深异常日志，线上固定返回 { code:500, message:"服务器内部错误" }，不泄露异常详情。
/// </summary>
public class GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger) : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        var deepest = context.Exception.GetDeepestException();
        logger.LogError(deepest, "未处理异常：{Method} {Path}",
            context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        context.Result = new ObjectResult(ApiResponse.Fail("服务器内部错误", 500))
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };
        context.ExceptionHandled = true;
    }
}
