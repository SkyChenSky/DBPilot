using DBPilot.AspNetCore.Filters;
using DBPilot.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace DBPilot.AspNetCore.Extension;

/// <summary>Web 模块：MVC + Newtonsoft（CamelCase / UTC ISO 8601，与前端约定一致）+ 全局异常过滤器 + Swagger（dev）+ 静态文件 + 登录认证（Web 必带）。</summary>
public static class WebExtension
{
    /// <summary>纯组合无开关（角色/模块开关在全家桶 AddDBPilot 或宿主组合根表达）；内含 AddDbpilotAuth（幂等）。</summary>
    public static WebApplicationBuilder AddDbpilotWeb(this WebApplicationBuilder builder)
    {
        builder.AddDbpilotAuth();        // Web 必带登录认证（TryAdd 幂等，全家桶已注册则跳过）

        builder.Services.AddControllers(options => options.Filters.Add<GlobalExceptionFilter>())
            .AddNewtonsoftJson(options =>
            {
                // SerializerSettings 为只读实例，逐项赋 SerializeExtension.CreateSettings() 的全部设置
                var settings = SerializeExtension.CreateSettings();
                options.SerializerSettings.ContractResolver = settings.ContractResolver;
                options.SerializerSettings.DateFormatHandling = settings.DateFormatHandling;
                options.SerializerSettings.DateFormatString = settings.DateFormatString;
                options.SerializerSettings.DateTimeZoneHandling = settings.DateTimeZoneHandling;
                options.SerializerSettings.NullValueHandling = settings.NullValueHandling;
                options.SerializerSettings.MissingMemberHandling = settings.MissingMemberHandling;
            });

        // Swagger 仅开发环境启用
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        return builder;
    }

    /// <summary>
    /// Swagger(dev) + 默认文档/静态文件（前端 SPA 资源，免登录访问）。
    /// 静态资产双保险：物理 wwwroot 优先（vite 直出 / nupkg targets 透传），库 DLL 内嵌清单兜底（项目引用场景）——
    /// 置换 WebRootFileProvider 后 UseDefaultFiles/UseStaticFiles/MapFallbackToFile 全部自动生效。
    /// </summary>
    public static WebApplication UseDbpilotWeb(this WebApplication app)
    {
        // 源码构建且未跑前端 build（wwwroot 空 → DLL 内嵌零资源）时跳过内嵌兜底——ManifestEmbeddedFileProvider 找不到清单会在构造期抛异常导致启动崩溃；
        // NuGet 包消费方清单恒存在，本守卫只在源码构建缺前端产物时生效
        var assembly = typeof(WebExtension).Assembly;
        if (Array.Exists(assembly.GetManifestResourceNames(), n => n.StartsWith("DBPilot.AspNetCore.wwwroot", StringComparison.Ordinal)))
        {
            app.Environment.WebRootFileProvider = new CompositeFileProvider(
                app.Environment.WebRootFileProvider,
                new ManifestEmbeddedFileProvider(assembly, "DBPilot.AspNetCore.wwwroot"));
        }
        else
        {
            app.Logger.LogWarning("前端资产未内嵌（源码构建需先 cd web && npm install && npm run build 再 dotnet build），Web UI 将不可用，API/采集不受影响");
        }

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseDefaultFiles();
        app.UseStaticFiles();

        return app;
    }
}
