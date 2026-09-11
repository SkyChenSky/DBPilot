using DBPilot.Common;
using DBPilot.Core.Instances;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// 实例管理。
/// </summary>
[Route("api")]
public class InstancesController(InstanceService service) : ApiControllerBase
{
    /// <summary>分页列表：page=1 / limit=10 / keyword 匹配名称或主机。</summary>
    [HttpGet("instances")]
    public async Task<IActionResult> List([FromQuery] PageListParams pageParams, string? keyword)
    {
        var page = await service.GetPageAsync(keyword, pageParams.Page, pageParams.Limit);
        if (page is null)
            return Ok(ApiResponse.Fail(InstanceConfigResolver.DbNotConfigured));

        return Ok(ApiResponse.Ok(page));
    }

    /// <summary>接入实例（凭据 AES-GCM 加密入库，成功后自动探测刷新状态）。</summary>
    [HttpPost("instances")]
    public async Task<IActionResult> Create([FromBody] InstanceSaveRequest request)
    {
        var result = await service.CreateAsync(request);
        return Ok(result);
    }

    /// <summary>更新实例（Password 为空 = 不修改密码）。</summary>
    [HttpPut("instances/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] InstanceSaveRequest request)
    {
        var result = await service.UpdateAsync(id, request);
        return Ok(result);
    }

    /// <summary>删除实例。</summary>
    [HttpDelete("instances/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await service.DeleteAsync(id);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : Ok(ApiResponse.Fail(result.Message));
    }

    /// <summary>测试未保存凭据（接入向导，不落库）：连通 + 权限自检（缺失权限与修复脚本）。</summary>
    [HttpPost("instances/test")]
    public async Task<IActionResult> TestUnsaved([FromBody] InstanceSaveRequest request)
        => Ok(ApiResponse.Ok(await service.TestUnsavedAsync(request)));

    /// <summary>测试已保存实例：连通 + 权限自检 + 版本元数据刷新。</summary>
    [HttpPost("instances/{id:int}/test")]
    public async Task<IActionResult> TestSaved(int id)
    {
        var result = await service.TestSavedAsync(id);
        return Ok(result);
    }

    /// <summary>库列表（索引诊断等页面下拉）。</summary>
    [HttpGet("instances/{id:int}/databases")]
    public async Task<IActionResult> Databases(int id)
    {
        var result = await service.GetDatabasesAsync(id);
        return Ok(result);
    }
}
