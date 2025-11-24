# Dashboard 集成示例 / Dashboard Integration Example

## 在 Example 项目中集成 Dashboard

### 1. 添加项目引用 / Add Project Reference

在 `Cyaim.WebSocketServer.Example.csproj` 中添加：

```xml
<ItemGroup>
  <ProjectReference Include="..\Dashboard\Cyaim.WebSocketServer.Dashboard\Cyaim.WebSocketServer.Dashboard.csproj" />
</ItemGroup>
```

### 2. 更新 Startup.cs / Update Startup.cs

在 `Cyaim.WebSocketServer.Example/Startup.cs` 中添加 Dashboard 配置：

```csharp
using Cyaim.WebSocketServer.Dashboard.Middlewares;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // 现有配置...
        services.AddControllers();
        
        // 添加 Dashboard 服务
        services.AddWebSocketDashboard();
        
        // WebSocketServer 配置...
        services.ConfigureWebSocketRoute(x => {
            // 现有配置
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // 现有配置...
        app.UseRouting();
        app.UseAuthorization();
        
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers(); // Dashboard API 需要
        });

        // WebSocketServer 配置...
        app.UseWebSockets(webSocketOptions);
        app.UseWebSocketServer();
        
        // 添加 Dashboard 中间件（必须在 MapControllers 之后）
        app.UseWebSocketDashboard("/dashboard");
    }
}
```

### 3. 完整示例 / Complete Example

```csharp
using System;
using System.Collections.Generic;
using Cyaim.WebSocketServer.Dashboard.Middlewares;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cyaim.WebSocketServer.Example
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            
            // 添加 Dashboard 服务
            services.AddWebSocketDashboard();
            
            // 配置 WebSocketServer
            services.ConfigureWebSocketRoute(x =>
            {
                var mvcHandler = new MvcChannelHandler();
                x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
                {
                    { "/ws", mvcHandler.ConnectionEntry }
                };
                x.ApplicationServiceCollection = services;
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers(); // Dashboard API 需要
            });

            // WebSocketServer 配置
            var webSocketOptions = new WebSocketOptions()
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15),
                ReceiveBufferSize = 4 * 1024
            };
            app.UseWebSockets(webSocketOptions);
            app.UseWebSocketServer();
            
            // Dashboard 中间件（必须在 MapControllers 之后）
            app.UseWebSocketDashboard("/dashboard");
        }
    }
}
```

## 运行步骤 / Running Steps

### 开发模式（前后端分离）

1. **启动后端**：
   ```bash
   cd Cyaim.WebSocketServer.Example
   dotnet run
   ```
   后端运行在 `http://localhost:5000`

2. **启动前端**（新终端）：
   ```bash
   cd Dashboard/websocketserver-dashboard
   pnpm dev
   ```
   前端运行在 `http://localhost:5173`

3. **访问**：`http://localhost:5173/dashboard/overview`

### 生产模式（前后端集成）

1. **构建前端**：
   ```bash
   cd Dashboard/websocketserver-dashboard
   pnpm build
   ```

2. **复制文件**：
   ```bash
   # 创建目录
   New-Item -ItemType Directory -Force -Path "..\Cyaim.WebSocketServer.Dashboard\wwwroot\public"
   
   # 复制文件
   Copy-Item -Path "build\*" -Destination "..\Cyaim.WebSocketServer.Dashboard\wwwroot\public\" -Recurse -Force
   ```

3. **启动后端**：
   ```bash
   cd Cyaim.WebSocketServer.Example
   dotnet run
   ```

4. **访问**：`http://localhost:5000/dashboard`

