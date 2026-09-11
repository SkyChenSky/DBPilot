// 等价 Microsoft.NET.Sdk.Web 的隐式 using（类库 + FrameworkReference 不自带，搬出 Host 后需显式补齐）
global using System.Net.Http;
global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Hosting;
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Routing;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
