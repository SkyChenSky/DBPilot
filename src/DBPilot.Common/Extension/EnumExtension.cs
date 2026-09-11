using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace DBPilot.Common;

/// <summary>
/// 枚举扩展方法（移植自 SF.Tookits.EnumExtension）。
/// </summary>
public static class EnumExtension
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    /// <summary>
    /// 获取枚举值的 [Description] 特性文本；无特性时返回枚举名。
    /// </summary>
    public static string GetDescription(this Enum input)
        => Cache.GetOrAdd($"{input.GetType()}-{input}", _ =>
        {
            var memberInfo = input.GetType().GetMember(input.ToString());
            if (memberInfo.Length == 0)
                return input.ToString();

            var attribute = memberInfo[0].GetCustomAttribute<DescriptionAttribute>();
            return attribute?.Description ?? input.ToString();
        }) ?? input.ToString();
}
