using Newtonsoft.Json;

namespace DBPilot.Common;

/// <summary>
/// 服务层响应实体：内部业务结果传递用，与线上的 ApiResponse（code/message/data）解耦。
/// Exception 不参与序列化，避免异常详情泄露到响应。
/// </summary>
public class ServiceResult
{
    public bool IsSuccess { get; set; }

    public string Message { get; set; } = string.Empty;

    public object? Data { get; set; }

    [JsonIgnore]
    public Exception? Exception { get; set; }

    public static ServiceResult Succeeded(string message = "ok", object? data = null)
        => new() { IsSuccess = true, Message = message, Data = data };

    public static ServiceResult Failed(string message, object? data = null)
        => new() { IsSuccess = false, Message = message, Data = data };

    public static ServiceResult ErrorException(Exception ex)
        => new() { IsSuccess = false, Message = ex.Message, Exception = ex };
}

/// <summary>泛型版本。</summary>
public class ServiceResult<T> : ServiceResult
{
    public new T? Data
    {
        get => (T?)base.Data;
        set => base.Data = value;
    }

    public static ServiceResult<T> Succeeded(T data, string message = "ok")
        => new() { IsSuccess = true, Message = message, Data = data };

    public static ServiceResult<T> Failed(string message, T? data = default)
        => new() { IsSuccess = false, Message = message, Data = data };

    public static new ServiceResult<T> ErrorException(Exception ex)
        => new() { IsSuccess = false, Message = ex.Message, Exception = ex };
}
