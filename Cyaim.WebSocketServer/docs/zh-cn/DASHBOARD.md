# Dashboard 文档

本文档详细介绍 Cyaim.WebSocketServer.Dashboard 的功能和使用方法。

## 目录

- [概述](#概述)
- [功能特性](#功能特性)
- [快速开始](#快速开始)
- [API 接口](#api-接口)
- [前端界面](#前端界面)
- [配置说明](#配置说明)
- [最佳实践](#最佳实践)

## 概述

Cyaim.WebSocketServer.Dashboard 是一个用于监控和管理 WebSocketServer 服务端（包含集群）的仪表板应用，提供：

- 📊 **实时监控** - 集群状态、连接数、消息统计
- 🖥️ **节点管理** - 查看和管理集群节点
- 👥 **客户端管理** - 查看所有客户端连接信息
- 📈 **性能分析** - 带宽监控、消息统计
- 🔄 **消息测试** - 发送测试消息

## 功能特性

### 后端功能

- ✅ **集群概览** - 查看所有节点状态、连接数、Raft 状态
- ✅ **节点管理** - 查看和管理集群节点
- ✅ **客户端管理** - 查看所有客户端连接信息、统计信息
- ✅ **消息发送** - 向指定连接发送测试消息
- ✅ **路由管理** - 查看连接路由表
- ✅ **统计信息** - 带宽统计、连接统计

### 前端功能

- ✅ **现代化 UI** - 基于 Svelte 5 和 Tailwind CSS
- ✅ **实时更新** - 自动刷新数据
- ✅ **响应式设计** - 支持移动端和桌面端
- ✅ **国际化支持** - 支持中文和英文

## 快速开始

### 1. 安装 NuGet 包

```bash
dotnet add package Cyaim.WebSocketServer.Dashboard
```

### 2. 配置服务

```csharp
using Cyaim.WebSocketServer.Dashboard.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// 添加 Dashboard 服务
builder.Services.AddWebSocketDashboard();

// 配置 WebSocketServer
builder.Services.ConfigureWebSocketRoute(x =>
{
    // WebSocketServer 配置
});
```

### 3. 配置中间件

```csharp
var app = builder.Build();

// 使用 Dashboard 中间件（必须在 MapControllers 之后）
app.UseWebSocketDashboard("/dashboard");
```

### 4. 访问 Dashboard

启动应用后，访问 `http://localhost:5000/dashboard` 查看 Dashboard。

## API 接口

### 集群接口

#### 获取集群概览

```http
GET /ws_server/api/dashboard/cluster/overview
```

响应：
```json
{
  "success": true,
  "data": {
    "totalNodes": 3,
    "connectedNodes": 3,
    "totalConnections": 100,
    "localConnections": 30,
    "currentNodeId": "node1",
    "isCurrentNodeLeader": true,
    "nodes": [...]
  }
}
```

#### 获取所有节点

```http
GET /ws_server/api/dashboard/cluster/nodes
```

#### 获取指定节点信息

```http
GET /ws_server/api/dashboard/cluster/nodes/{nodeId}
```

### 客户端接口

#### 获取所有客户端

```http
GET /ws_server/api/client
```

查询参数：
- `nodeId` (可选): 节点 ID 过滤器

#### 获取指定节点的客户端

```http
GET /ws_server/api/client?nodeId=node1
```

#### 获取连接数统计

```http
GET /ws_server/api/client/count
```

响应：
```json
{
  "success": true,
  "data": {
    "total": 100,
    "local": 30
  }
}
```

### 消息接口

#### 发送消息

```http
POST /ws_server/api/messages/send
Content-Type: application/json

{
  "connectionId": "connection-id",
  "content": "Hello, World!",
  "messageType": "Text"
}
```

#### 广播消息

```http
POST /ws_server/api/messages/broadcast
Content-Type: application/json

{
  "content": "Broadcast message",
  "messageType": "Text"
}
```

#### 向指定节点广播

```http
POST /ws_server/api/messages/broadcast/node/{nodeId}
Content-Type: application/json

{
  "content": "Node broadcast",
  "messageType": "Text"
}
```

### 路由接口

#### 获取路由表

```http
GET /ws_server/api/routes
```

响应：
```json
{
  "success": true,
  "data": {
    "connection-id-1": "node1",
    "connection-id-2": "node2"
  }
}
```

#### 查询连接所在节点

```http
GET /ws_server/api/routes/{connectionId}
```

### 统计接口

#### 获取带宽统计

```http
GET /ws_server/api/statistics/bandwidth
```

响应：
```json
{
  "success": true,
  "data": {
    "totalBytesSent": 1024000,
    "totalBytesReceived": 2048000,
    "totalMessagesSent": 1000,
    "totalMessagesReceived": 2000,
    "bytesSentPerSecond": 1024,
    "bytesReceivedPerSecond": 2048,
    "messagesSentPerSecond": 10,
    "messagesReceivedPerSecond": 20
  }
}
```

#### 获取连接统计

```http
GET /ws_server/api/statistics/connections
```

## 前端界面

### 技术栈

- **框架**: SvelteKit 2.x
- **UI 库**: Svelte 5
- **样式**: Tailwind CSS 4.x
- **国际化**: Paraglide.js (inlang)
- **构建工具**: Vite 7.x

### 页面结构

```
dashboard/
├── overview/      # 集群概览
├── nodes/        # 节点管理
├── clients/      # 客户端列表
├── bandwidth/    # 带宽监控
├── dataflow/     # 数据流查看
└── send/         # 消息发送
```

### 开发

```bash
cd Dashboard/websocketserver-dashboard
pnpm install
pnpm dev
```

### 构建

```bash
pnpm build
```

## 配置说明

### 服务注册

```csharp
builder.Services.AddWebSocketDashboard();
```

这会注册：
- `DashboardStatisticsService` - 统计服务
- `DashboardHelperService` - 辅助服务
- `IWebSocketStatisticsRecorder` - 统计记录器接口

### 中间件配置

```csharp
app.UseWebSocketDashboard("/dashboard");
```

参数：
- `pathPrefix` (可选): Dashboard 路径前缀，默认 `/dashboard`

### CORS 配置

如果前端和后端分离部署，需要配置 CORS：

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

## 最佳实践

### 1. 性能优化

- 合理设置数据刷新间隔
- 使用分页加载大量数据
- 缓存静态数据

### 2. 安全考虑

- 在生产环境中限制 Dashboard 访问
- 使用身份验证和授权
- 配置 HTTPS

### 3. 监控告警

- 设置关键指标告警
- 监控 Dashboard 性能
- 记录访问日志

### 4. 用户体验

- 提供清晰的错误提示
- 优化加载速度
- 支持响应式设计

## 相关文档

- [核心库文档](./CORE.md)
- [集群模块文档](./CLUSTER.md)
- [指标统计文档](./METRICS.md)
- [API 参考](./API_REFERENCE.md)

