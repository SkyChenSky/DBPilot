using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DBPilot.Common;

/// <summary>
/// JSON 统一序列化（Newtonsoft）：
/// - 属性名 CamelCase；
/// - 日期统一 UTC、ISO 8601（yyyy-MM-dd'T'HH:mm:ss'Z'）。
/// 后端 MVC 与前端约定共用同一套规则（CreateSettings）。
/// </summary>
public static class SerializeExtension
{
    /// <summary>统一序列化设置：CamelCase + UTC ISO 8601（MVC / 扩展方法共用）。</summary>
    public static JsonSerializerSettings CreateSettings() => new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy(processDictionaryKeys: true, overrideSpecifiedNames: true)
        },
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateFormatString = "yyyy-MM-dd'T'HH:mm:ss'Z'",
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        NullValueHandling = NullValueHandling.Include,
        MissingMemberHandling = MissingMemberHandling.Ignore
    };

    /// <summary>对象转 JSON 字符串；null 返回空串。</summary>
    public static string ToJson(this object? obj)
        => obj is null ? string.Empty : JsonConvert.SerializeObject(obj, CreateSettings());

    /// <summary>JSON 字符串转对象；空串返回 default。</summary>
    public static T? FromJson<T>(this string? jsonStr)
        => jsonStr.IsNullOrEmpty() ? default : JsonConvert.DeserializeObject<T>(jsonStr, CreateSettings());

    /// <summary>JSON 字符串转对象列表；空串返回空列表。</summary>
    public static List<T> ToList<T>(this string? jsonStr)
        => jsonStr.IsNullOrEmpty() ? [] : JsonConvert.DeserializeObject<List<T>>(jsonStr, CreateSettings()) ?? [];
}
