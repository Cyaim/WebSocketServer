# WebSocketServer Dashboard 示例项目 / Dashboard Sample Project

这是一个完整的 Dashboard 示例项目，展示了如何集成和使用 WebSocketServer Dashboard。

This is a complete Dashboard sample project demonstrating how to integrate and use WebSocketServer Dashboard.

## 📋 项目结构 / Project Structure

```
Sample/
└── Cyaim.WebSocketServer.Sample.Dashboard/
    ├── Program.cs                          # 应用程序入口 / Application entry point
    ├── Controllers/
    │   └── EchoController.cs              # WebSocket 控制器示例 / WebSocket controller example
    ├── appsettings.json                   # 应用配置 / Application configuration
    ├── appsettings.Development.json       # 开发环境配置 / Development environment configuration
    ├── Properties/
    │   └── launchSettings.json            # 启动配置 / Launch settings
    └── README.md                          # 本文件 / This file
```

## 🚀 快速开始 / Quick Start

### 1. 运行后端 / Run Backend

```bash
cd Sample/Cyaim.WebSocketServer.Sample.Dashboard
dotnet run
```

后端将运行在：`http://localhost:5000`

### 2. 运行前端（开发模式） / Run Frontend (Development Mode)

打开新的终端窗口：

```bash
cd Dashboard/websocketserver-dashboard
pnpm install  # 首次运行需要 / Required for first run
pnpm dev
```

前端将运行在：`http://localhost:5173`

### 3. 访问 Dashboard / Access Dashboard

打开浏览器访问：**http://localhost:5173/dashboard/overview**

## 📝 功能说明 / Features

### WebSocket 端点 / WebSocket Endpoints

- `/ws` - WebSocket 连接端点

### WebSocket 操作 / WebSocket Actions

- `echo` - 回显消息
  ```json
  {
    "action": "echo",
    "data": "Hello, World!"
  }
  ```

- `time` - 获取服务器时间
  ```json
  {
    "action": "time"
  }
  ```

### Dashboard 功能 / Dashboard Features

- 📊 **集群概览** / Cluster Overview
- 🖥️ **节点管理** / Node Management
- 👥 **客户端管理** / Client Management
- 📈 **带宽监控** / Bandwidth Monitoring
- 🔄 **数据流查看** / Data Flow Viewer
- 📤 **消息发送** / Message Sender

## 🔧 配置说明 / Configuration

### Dashboard 路径 / Dashboard Path

默认 Dashboard 路径为 `/dashboard`，可以在 `Program.cs` 中修改：

```csharp
app.UseWebSocketDashboard("/your-custom-path");
```

### WebSocket 通道 / WebSocket Channels

WebSocket 通道在 `Program.cs` 中配置：

```csharp
x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
{
    { "/ws", mvcHandler.ConnectionEntry }
};
```

## 📚 代码说明 / Code Explanation

### Program.cs

这是应用程序的主入口文件，包含：

1. **服务配置** / Service Configuration：
   - `AddControllers()` - 添加 MVC 控制器支持
   - `AddWebSocketDashboard()` - 添加 Dashboard 服务
   - `ConfigureWebSocketRoute()` - 配置 WebSocket 路由

2. **中间件配置** / Middleware Configuration：
   - `UseRouting()` - 启用路由
   - `MapControllers()` - 映射控制器（Dashboard API 需要）
   - `UseWebSockets()` - 启用 WebSocket
   - `UseWebSocketServer()` - 启用 WebSocketServer
   - `UseWebSocketDashboard()` - 启用 Dashboard（必须在 MapControllers 之后）

### EchoController.cs

这是一个简单的 WebSocket 控制器示例，展示了：

- 如何实现 `IWebSocketSession` 接口
- 如何定义 `WebSocketHttpContext` 和 `WebSocketClient` 属性
- 如何使用 `[WebSocket]` 特性标记方法
- 如何通过 `WebSocketClient.SendAsync()` 发送消息

## 🧪 测试 / Testing

### 使用 WebSocket 客户端测试 / Test with WebSocket Client

可以使用任何 WebSocket 客户端工具（如 Postman、WebSocket King）连接到：

```
ws://localhost:5000/ws
```

然后发送 JSON 消息：

```json
{
  "action": "echo",
  "data": "Hello, Dashboard!"
}
```

### 使用浏览器测试 / Test with Browser

打开浏览器控制台，运行：

```javascript
const ws = new WebSocket('ws://localhost:5000/ws');
ws.onopen = () => {
  console.log('Connected');
  ws.send(JSON.stringify({ action: 'echo', data: 'Hello!' }));
};
ws.onmessage = (event) => {
  console.log('Received:', event.data);
};
```

## 📖 更多信息 / More Information

- Dashboard 详细文档：`../Dashboard/README.md`
- 快速开始指南：`../Dashboard/QUICK_START.md`
- WebSocketServer 文档：查看主项目文档

## ⚠️ 注意事项 / Notes

1. **中间件顺序**：`UseWebSocketDashboard` 必须在 `MapControllers` 之后
2. **CORS 配置**：如果前后端分离运行，可能需要配置 CORS
3. **静态文件**：生产模式需要将前端构建文件复制到 `wwwroot/public` 目录

## 🎯 下一步 / Next Steps

1. 尝试添加更多 WebSocket 操作
2. 配置集群功能（如果需要）
3. 自定义 Dashboard 界面
4. 添加身份验证和授权

