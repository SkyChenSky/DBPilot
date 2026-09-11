namespace DBPilot.Common;

/// <summary>
/// 统一 API 响应模型：{ code, message, data }。
/// code = 0 表示成功，非 0 表示业务失败。
/// </summary>
public class ApiResponse
{
    public const int SuccessCode = 0;
    public const int DefaultErrorCode = 1;

    public int Code { get; set; }

    public string Message { get; set; } = string.Empty;

    public object? Data { get; set; }

    /// <summary>是否成功（Code == 0）。</summary>
    public bool IsSuccess => Code == SuccessCode;

    public static ApiResponse Ok(object? data = null)
        => new() { Code = SuccessCode, Message = "ok", Data = data };

    public static ApiResponse Fail(string message, int code = DefaultErrorCode)
        => new() { Code = code, Message = message };
}

/// <summary>强类型版本，便于服务层与前端泛型封装。</summary>
public class ApiResponse<T> : ApiResponse
{
    public new T? Data
    {
        get => (T?)base.Data;
        set => base.Data = value;
    }

    public static ApiResponse<T> Ok(T data)
        => new() { Code = SuccessCode, Message = "ok", Data = data };

    public static new ApiResponse<T> Fail(string message, int code = DefaultErrorCode)
        => new() { Code = code, Message = message };
}
