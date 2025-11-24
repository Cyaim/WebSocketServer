# 运行指南 / Running Guide

## 快速开始 / Quick Start

### 方式一：开发模式（推荐） / Development Mode (Recommended)

前后端分离运行，便于开发调试。

#### 1. 启动后端 / Start Backend

```bash
# 进入示例项目目录
cd ../../Cyaim.WebSocketServer.Example

# 运行后端
dotnet run
```

后端将运行在 `http://localhost:5000`

**注意**：确保在 `Startup.cs` 或 `Program.cs` 中已配置 Dashboard：

```csharp
// 在 ConfigureServices 中添加
services.AddWebSocketDashboard();

// 在 Configure 或 Program.cs 中添加（必须在 MapControllers 之后）
app.UseWebSocketDashboard("/dashboard");
```

#### 2. 启动前端 / Start Frontend

```bash
# 进入前端项目目录
cd Dashboard/websocketserver-dashboard

# 安装依赖（首次运行）
pnpm install

# 启动开发服务器
pnpm dev
```

前端将运行在 `http://localhost:5173`

#### 3. 访问 Dashboard / Access Dashboard

打开浏览器访问：`http://localhost:5173/dashboard/overview`

### 方式二：生产模式 / Production Mode

前端构建后集成到后端，单一部署。

#### 1. 构建前端 / Build Frontend

```bash
cd Dashboard/websocketserver-dashboard
pnpm install
pnpm build
```

#### 2. 复制构建文件 / Copy Build Files

```bash
# Windows PowerShell
New-Item -ItemType Directory -Force -Path "..\Cyaim.WebSocketServer.Dashboard\wwwroot\public"
Copy-Item -Path "build\*" -Destination "..\Cyaim.WebSocketServer.Dashboard\wwwroot\public\" -Recurse -Force

# Linux/Mac
mkdir -p ../Cyaim.WebSocketServer.Dashboard/wwwroot/public
cp -r build/* ../Cyaim.WebSocketServer.Dashboard/wwwroot/public/
```

#### 3. 启动后端 / Start Backend

```bash
cd ../../Cyaim.WebSocketServer.Example
dotnet run
```

#### 4. 访问 Dashboard / Access Dashboard

打开浏览器访问：`http://localhost:5000/dashboard`

## 后端配置示例 / Backend Configuration Example

### .NET 6+ (Program.cs)

```csharp
using Cyaim.WebSocketServer.Dashboard.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// 添加服务
builder.Services.AddControllers();
builder.Services.AddWebSocketDashboard(); // 添加 Dashboard

// 配置 WebSocketServer
builder.Services.ConfigureWebSocketRoute(x => {
    var mvcHandler = new MvcChannelHandler();
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }
    };
});

var app = builder.Build();

// 配置中间件
app.UseRouting();
app.UseWebSockets();
app.UseWebSocketServer();

app.MapControllers(); // Dashboard API 需要

app.UseWebSocketDashboard("/dashboard"); // 必须在 MapControllers 之后

app.Run();
```

### .NET Core 3.x / .NET 5 (Startup.cs)

```csharp
using Cyaim.WebSocketServer.Dashboard.Middlewares;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers();
        services.AddWebSocketDashboard(); // 添加 Dashboard
        
        services.ConfigureWebSocketRoute(x => {
            // WebSocketServer 配置
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();
        app.UseWebSockets();
        app.UseWebSocketServer();
        
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers(); // Dashboard API 需要
        });
        
        app.UseWebSocketDashboard("/dashboard"); // 必须在 UseEndpoints 之后
    }
}
```

## 故障排除 / Troubleshooting

### 前端无法连接后端 API

1. 检查后端是否运行在 `http://localhost:5000`
2. 检查 `vite.config.ts` 中的代理配置
3. 检查浏览器控制台的网络请求

### 后端无法找到 Dashboard 文件

1. 确保 `wwwroot/public` 目录存在
2. 确保前端构建文件已正确复制
3. 检查文件路径是否正确

### CORS 错误

如果前后端分离运行，需要配置 CORS：

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowDashboard", policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

app.UseCors("AllowDashboard");
```

## 开发提示 / Development Tips

1. **热重载**：前端使用 Vite，支持热重载，修改代码后自动刷新
2. **API 调试**：可以使用浏览器开发者工具查看 API 请求和响应
3. **日志**：后端日志会显示 Dashboard API 的请求信息

