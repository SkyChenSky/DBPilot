using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// 健康检查：GET /health 与 GET /api/health 均返回 {"status":"ok"}。
/// </summary>
[Route("health")]
[Route("api/health")]
public class HealthController : ApiControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok" });
}
