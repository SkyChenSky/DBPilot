using DBPilot.Common;
using DBPilot.Core.Providers;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// 引擎能力矩阵：GET /api/engines → { engine: { 能力键: 'full'|'degraded'|'none' } }。
/// 后端单一来源（引擎包 [DbpilotCapabilities] 自声明，未声明的键默认 full），前端据此做入口减法与
/// 同页降级形态；只输出已注册引擎，未注册引擎前端按全 none 处理（fail-closed 对齐 ProviderRegistry）。
/// </summary>
[Route("api/engines")]
public class EnginesController(ProviderRegistry registry) : ApiControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var data = registry.Capabilities.ToDictionary(
            kv => kv.Key,
            kv => DbpilotCapabilityKeys.All.ToDictionary(
                key => key,
                key => registry.CapabilityOf(kv.Key, key).ToString().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        return Ok(ApiResponse.Ok(data));
    }
}
