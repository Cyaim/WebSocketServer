# WebSocketServer Dashboard

WebSocketServer Dashboard 是一个用于监控和管理 WebSocketServer 服务端（包含集群）的仪表板应用。

## 功能特性 / Features

- 📊 **集群概览** / Cluster Overview: 查看所有节点状态、连接数、Raft 状态等
- 🖥️ **节点管理** / Node Management: 查看和管理集群节点
- 👥 **客户端管理** / Client Management: 查看所有客户端连接信息、统计信息
- 📈 **带宽监控** / Bandwidth Monitoring: 实时监控网络带宽使用情况
- 🔄 **数据流查看** / Data Flow Viewer: 查看实时数据流消息
- 📤 **消息发送** / Message Sender: 向指定连接发送测试消息
- 🎨 **现代化 UI** / Modern UI: 基于 Svelte 5 和 Tailwind CSS 构建的响应式界面
- 🌐 **国际化支持** / i18n Support: 支持中文和英文双语

## 后端配置 / Backend Configuration

### 1. 安装 NuGet 包 / Install NuGet Package

```bash
dotnet add package Cyaim.WebSocketServer.Dashboard
```

### 2. 在 Startup.cs 或 Program.cs 中配置 / Configure in Startup.cs or Program.cs

#### 方式一：使用 Startup.cs (适用于 .NET Core 3.x / .NET 5)

```csharp
using Cyaim.WebSocketServer.Dashboard.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // 其他服务配置...
        services.AddControllers();
        
        // 添加 Dashboard 服务
        services.AddWebSocketDashboard();
        
        // 配置 WebSocketServer...
        services.ConfigureWebSocketRoute(x => {
            // WebSocketServer 配置
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // 其他中间件...
        app.UseRouting();
        app.UseWebSockets();
        app.UseWebSocketServer();
        
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers(); // 必须添加，用于 Dashboard API
        });
        
        // 使用 Dashboard 中间件（必须在 MapControllers 之后）
        app.UseWebSocketDashboard("/dashboard");
    }
}
```

#### 方式二：使用 Program.cs (适用于 .NET 6+)

```csharp
using Cyaim.WebSocketServer.Dashboard.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// 添加服务
builder.Services.AddControllers();
builder.Services.AddWebSocketDashboard(); // 添加 Dashboard 服务

// 配置 WebSocketServer
builder.Services.ConfigureWebSocketRoute(x => {
    // WebSocketServer 配置
});

var app = builder.Build();

// 配置中间件
app.UseRouting();
app.UseWebSockets();
app.UseWebSocketServer();

app.MapControllers(); // 必须添加，用于 Dashboard API

// 使用 Dashboard 中间件（必须在 MapControllers 之后）
app.UseWebSocketDashboard("/dashboard");

app.Run();
```

### 3. 配置静态文件服务（可选，用于部署前端） / Configure Static Files (Optional)

如果需要将前端构建后的文件部署到后端，需要配置静态文件服务：

```csharp
// 在 Configure 或 Program.cs 中添加
app.UseStaticFiles(); // 如果需要提供静态文件

// 或者指定 wwwroot 目录
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(builder.Environment.ContentRootPath, "wwwroot")),
    RequestPath = "/dashboard"
});
```

## 前端配置 / Frontend Configuration

### 开发模式 / Development Mode

#### 1. 安装依赖

```bash
cd Dashboard/websocketserver-dashboard
pnpm install
```

#### 2. 配置 API 代理（开发时使用）

创建或更新 `vite.config.ts`，添加代理配置：

```typescript
import { defineConfig } from 'vite';
import { sveltekit } from '@sveltejs/kit/vite';

export default defineConfig({
  plugins: [sveltekit()],
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5000', // 后端 API 地址
        changeOrigin: true
      }
    }
  }
});
```

#### 3. 启动开发服务器

```bash
pnpm dev
```

前端将在 `http://localhost:5173` 运行，API 请求会自动代理到后端。

### 生产模式 / Production Mode

#### 1. 构建前端

```bash
cd Dashboard/websocketserver-dashboard
pnpm build
```

构建后的文件将输出到 `build` 目录。

#### 2. 复制构建文件到后端

将构建后的文件复制到后端的 `wwwroot` 目录：

```bash
# Windows PowerShell
Copy-Item -Path "build\*" -Destination "..\Cyaim.WebSocketServer.Dashboard\wwwroot\public\" -Recurse -Force

# Linux/Mac
cp -r build/* ../Cyaim.WebSocketServer.Dashboard/wwwroot/public/
```

#### 3. 更新后端中间件配置

确保 `DashboardMiddleware` 能够正确提供静态文件（已自动配置）。

## 运行步骤 / Running Steps

### 方式一：开发模式（前后端分离）

1. **启动后端**：
   ```bash
   cd Cyaim.WebSocketServer.Example
   dotnet run
   ```
   后端运行在 `http://localhost:5000`

2. **启动前端**：
   ```bash
   cd Dashboard/websocketserver-dashboard
   pnpm dev
   ```
   前端运行在 `http://localhost:5173`

3. **访问 Dashboard**：
   打开浏览器访问 `http://localhost:5173/dashboard/overview`

### 方式二：生产模式（前后端集成）

1. **构建前端**：
   ```bash
   cd Dashboard/websocketserver-dashboard
   pnpm build
   ```

2. **复制文件到后端**：
   ```bash
   # 确保后端项目有 wwwroot/public 目录
   Copy-Item -Path "build\*" -Destination "..\Cyaim.WebSocketServer.Dashboard\wwwroot\public\" -Recurse -Force
   ```

3. **启动后端**：
   ```bash
   cd Cyaim.WebSocketServer.Example
   dotnet run
   ```

4. **访问 Dashboard**：
   打开浏览器访问 `http://localhost:5000/dashboard`

## API 端点 / API Endpoints

Dashboard 提供以下 API 端点：

- `GET /api/dashboard/cluster/overview` - 获取集群概览
- `GET /api/dashboard/cluster/nodes` - 获取节点列表
- `GET /api/dashboard/clients` - 获取客户端连接列表
- `GET /api/dashboard/bandwidth` - 获取带宽统计信息
- `POST /api/dashboard/send` - 发送消息到指定连接

## 注意事项 / Notes

1. **CORS 配置**：如果前后端分离运行，需要配置 CORS：
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

2. **静态文件路径**：确保 `wwwroot/public` 目录存在，用于存放前端构建文件。

3. **API 路径**：前端 API 客户端默认使用 `/api/dashboard`，如需修改，请更新 `src/lib/api/dashboard.ts` 中的 `API_BASE_URL`。

## 许可证 / License

Copyright © Cyaim Studio
