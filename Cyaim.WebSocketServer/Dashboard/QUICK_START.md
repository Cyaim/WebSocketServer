# Dashboard 快速开始指南 / Quick Start Guide

## 📋 前置要求 / Prerequisites

- .NET SDK 6.0 或更高版本
- Node.js 18+ 和 pnpm
- 已配置的 WebSocketServer 项目

## 🚀 快速开始 / Quick Start

### 方式一：开发模式（推荐，前后端分离） / Development Mode (Recommended)

#### 步骤 1：配置后端 / Configure Backend

在 `Cyaim.WebSocketServer.Example/Startup.cs` 中添加：

```csharp
using Cyaim.WebSocketServer.Dashboard.Middlewares;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers();
        
        // ✅ 添加这一行
        services.AddWebSocketDashboard();
        
        // 您现有的 WebSocketServer 配置...
        services.ConfigureWebSocketRoute(x => {
            // ...
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
        
        // WebSocketServer 配置...
        app.UseWebSockets();
        app.UseWebSocketServer();
        
        // ✅ 添加这一行（必须在 MapControllers 之后）
        app.UseWebSocketDashboard("/dashboard");
    }
}
```

#### 步骤 2：添加项目引用 / Add Project Reference

在 `Cyaim.WebSocketServer.Example/Cyaim.WebSocketServer.Example.csproj` 中添加：

```xml
<ItemGroup>
  <ProjectReference Include="..\Dashboard\Cyaim.WebSocketServer.Dashboard\Cyaim.WebSocketServer.Dashboard.csproj" />
</ItemGroup>
```

#### 步骤 3：启动后端 / Start Backend

```bash
cd Cyaim.WebSocketServer.Example
dotnet run
```

后端将运行在 `http://localhost:5000`

#### 步骤 4：启动前端 / Start Frontend

打开新的终端窗口：

```bash
cd Dashboard/websocketserver-dashboard
pnpm install  # 首次运行需要
pnpm dev
```

前端将运行在 `http://localhost:5173`

#### 步骤 5：访问 Dashboard / Access Dashboard

打开浏览器访问：**http://localhost:5173/dashboard/overview**

---

### 方式二：生产模式（前后端集成） / Production Mode

#### 步骤 1-2：同开发模式 / Same as Development Mode

配置后端和添加项目引用（同上）

#### 步骤 3：构建前端 / Build Frontend

```bash
cd Dashboard/websocketserver-dashboard
pnpm install
pnpm build
```

#### 步骤 4：复制构建文件 / Copy Build Files

```bash
# Windows PowerShell
$dashboardPath = "Dashboard\Cyaim.WebSocketServer.Dashboard\wwwroot\public"
New-Item -ItemType Directory -Force -Path $dashboardPath
Copy-Item -Path "build\*" -Destination $dashboardPath -Recurse -Force

# Linux/Mac
mkdir -p Dashboard/Cyaim.WebSocketServer.Dashboard/wwwroot/public
cp -r build/* Dashboard/Cyaim.WebSocketServer.Dashboard/wwwroot/public/
```

#### 步骤 5：启动后端 / Start Backend

```bash
cd Cyaim.WebSocketServer.Example
dotnet run
```

#### 步骤 6：访问 Dashboard / Access Dashboard

打开浏览器访问：**http://localhost:5000/dashboard**

---

## 🔧 配置说明 / Configuration

### API 路径配置 / API Path Configuration

前端默认使用 `/api/dashboard` 作为 API 基础路径。

如需修改，编辑 `Dashboard/websocketserver-dashboard/src/lib/api/dashboard.ts`：

```typescript
const API_BASE_URL = '/api/dashboard'; // 修改这里
```

### Dashboard 路径配置 / Dashboard Path Configuration

后端默认 Dashboard 路径为 `/dashboard`。

如需修改，在 `Startup.cs` 中：

```csharp
app.UseWebSocketDashboard("/your-custom-path");
```

### CORS 配置（开发模式需要） / CORS Configuration

如果前后端分离运行，需要配置 CORS：

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddCors(options =>
    {
        options.AddPolicy("AllowDashboard", policy =>
        {
            policy.WithOrigins("http://localhost:5173")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });
    
    // 其他配置...
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    app.UseCors("AllowDashboard"); // 添加这一行
    
    // 其他配置...
}
```

---

## 📝 完整示例 / Complete Example

### Startup.cs 完整配置

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

---

## ❓ 常见问题 / FAQ

### Q: 前端无法连接到后端 API？

**A:** 检查以下几点：
1. 后端是否正在运行在 `http://localhost:5000`
2. `vite.config.ts` 中的代理配置是否正确
3. 浏览器控制台是否有 CORS 错误（如有，需要配置 CORS）

### Q: 访问 Dashboard 显示 404？

**A:** 确保：
1. `app.UseWebSocketDashboard("/dashboard")` 已添加
2. 中间件顺序正确（必须在 `MapControllers` 之后）
3. 如果使用生产模式，确保前端文件已正确复制到 `wwwroot/public`

### Q: API 返回错误？

**A:** 检查：
1. `services.AddWebSocketDashboard()` 已添加
2. `endpoints.MapControllers()` 已添加
3. WebSocketServer 已正确配置

---

## 📚 更多信息 / More Information

- 详细配置：查看 `README.md`
- 集成示例：查看 `INTEGRATION_EXAMPLE.md`
- 运行指南：查看 `RUNNING.md`

