using DBPilot.Common;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// API 控制器基类：统一 [ApiController] 行为 + ServiceResult → ApiResponse 统一转换。
/// </summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>ServiceResult → HTTP 200 + ApiResponse（成功带 data / 失败带 message，前端按 code 分流）。</summary>
    protected IActionResult Ok<T>(ServiceResult<T> result)
        => result.IsSuccess ? Ok(ApiResponse.Ok(result.Data)) : Ok(ApiResponse.Fail(result.Message));
}
