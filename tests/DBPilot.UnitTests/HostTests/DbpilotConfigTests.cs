using DBPilot.AspNetCore.Extension;
using DBPilot.AspNetCore.Scheduling;
using Microsoft.Extensions.Configuration;

namespace DBPilot.UnitTests.HostTests;

/// <summary>
/// 配置树整合与角色开关：Job cron 默认合并语义 + AddDBPilot 自动读取的 Roles/AutoInitSchema 默认开关。
/// </summary>
public class DbpilotConfigTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings)
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(settings.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)));
        return builder.Build();
    }

    [Fact]
    public void JobCron合并_配置缺失_返回默认全集()
    {
        var merged = QuartzExtension.MergeJobCrons(null);

        Assert.Equal(QuartzExtension.DefaultJobCrons.Count, merged.Count);
        Assert.Contains("InstanceMetrics", merged.Keys);   // 模块 Job 默认节奏一并内置
        Assert.Equal("0/10 * * * * ?", merged["SessionSample"]);
    }

    [Fact]
    public void JobCron合并_配置覆盖默认_未实现的Job忽略()
    {
        var merged = QuartzExtension.MergeJobCrons(new Dictionary<string, string?>
        {
            ["SessionSample"] = "0/30 * * * * ?",
            ["NotAJob"] = "0 * * * * ?",
        });

        Assert.Equal("0/30 * * * * ?", merged["SessionSample"]);        // 覆盖
        Assert.DoesNotContain("NotAJob", merged.Keys);                   // 未实现忽略
        Assert.Equal(QuartzExtension.DefaultJobCrons.Count, merged.Count);
    }

    [Fact]
    public void JobCron合并_显式空值_禁用该Job()
    {
        foreach (var empty in new[] { (string?)"", null, "  " })
        {
            var merged = QuartzExtension.MergeJobCrons(new Dictionary<string, string?> { ["Deadlock"] = empty });

            Assert.DoesNotContain("Deadlock", merged.Keys);              // 禁用 = 从结果移除
            Assert.Equal(QuartzExtension.DefaultJobCrons.Count - 1, merged.Count);
        }
    }

    [Fact]
    public void 角色解析_配置缺失_默认双开()
    {
        var options = DbpilotOptions.ReadDefaults(Config());

        Assert.True(options.Web);
        Assert.True(options.Collector);
        Assert.True(options.InitSchema);
    }

    [Fact]
    public void 角色解析_仅Web_或_仅Collector()
    {
        Assert.False(DbpilotOptions.ReadDefaults(Config(("DBPilot:Roles:Collector", "false"))).Collector);
        Assert.True(DbpilotOptions.ReadDefaults(Config(("DBPilot:Roles:Collector", "false"))).Web);

        Assert.False(DbpilotOptions.ReadDefaults(Config(("DBPilot:Roles:Web", "false"))).Web);
        Assert.True(DbpilotOptions.ReadDefaults(Config(("DBPilot:Roles:Web", "false"))).Collector);
        Assert.False(DbpilotOptions.ReadDefaults(Config(("DBPilot:AutoInitSchema", "false"))).InitSchema);
    }
}
