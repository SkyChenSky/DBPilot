using Mapster;
using MapsterMapper;

namespace DBPilot.Common;

/// <summary>
/// 对象映射扩展（Mapster）：属性同名自动映射，异名 / 计算列经 TypeAdapterConfig 全局配置
/// （配置方：各服务静态构造注册，见 SampleFlushService 等）。
/// </summary>
public static class MapperExtension
{
    static MapperExtension()
    {
        TypeAdapterConfig<DateTimeOffset, DateTime>.NewConfig()
            .MapWith(dateTimeOffset => dateTimeOffset.LocalDateTime);
        TypeAdapterConfig<DateTimeOffset?, DateTime?>.NewConfig()
            .MapWith(dateTimeOffset => dateTimeOffset.HasValue ? dateTimeOffset.Value.LocalDateTime : null);
    }

    /// <summary>对象映射（源类型显式，避免协变误判）。</summary>
    public static TResult MapTo<TFrom, TResult>(this TFrom obj)
        => new Mapper().Map<TFrom, TResult>(obj);

    /// <summary>对象映射（源类型 object，按运行时类型映射）。</summary>
    public static TResult MapTo<TResult>(this object obj)
        => new Mapper().Map<TResult>(obj);

    /// <summary>对象映射到既有实例（覆盖同名属性）。</summary>
    public static void MapTo<TFrom, TResult>(this object source, TResult result) where TFrom : class
        => new Mapper().Map(source as TFrom, result);
}
