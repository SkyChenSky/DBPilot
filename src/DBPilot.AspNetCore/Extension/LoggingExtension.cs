using Serilog;

namespace DBPilot.AspNetCore.Extension;

/// <summary>日志模块：Serilog 控制台 + 按天滚动文件（logs/dbpilot-yyyyMMdd.log，保留 15 天）。</summary>
public static class LoggingExtension
{
    public static WebApplicationBuilder AddDbpilotLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((ctx, cfg) => cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(ctx.HostingEnvironment.ContentRootPath, "logs", "dbpilot-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 15));
        return builder;
    }
}
