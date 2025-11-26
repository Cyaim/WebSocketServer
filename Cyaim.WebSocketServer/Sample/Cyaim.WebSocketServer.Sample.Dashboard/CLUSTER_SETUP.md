# WebSocket 集群配置说明

本文档说明如何配置和启动3个集群节点来测试集群功能。

## 📋 配置说明

已为3个节点创建了独立的配置文件：

- `appsettings.node1.json` - 节点1配置（端口 5001）
- `appsettings.node2.json` - 节点2配置（端口 5002）
- `appsettings.node3.json` - 节点3配置（端口 5003）

每个配置文件包含：
- 节点ID和监听地址
- 集群传输类型（WebSocket）
- 其他节点的连接信息

## 🚀 快速启动

### 方法1: 使用启动脚本（推荐）

#### PowerShell 脚本
```powershell
.\start-cluster.ps1
```

#### 批处理脚本
```cmd
start-cluster.bat
```

脚本会自动：
1. 检查可执行文件是否存在
2. 依次启动3个节点实例
3. 显示各节点的访问地址
4. 等待用户按键后停止所有节点

### 方法2: 手动启动

#### 编译项目
```bash
dotnet build
```

#### 启动节点1
```bash
.\bin\Debug\net8.0\Cyaim.WebSocketServer.Sample.Dashboard.exe --node=node1
```

#### 启动节点2（新终端窗口）
```bash
.\bin\Debug\net8.0\Cyaim.WebSocketServer.Sample.Dashboard.exe --node=node2
```

#### 启动节点3（新终端窗口）
```bash
.\bin\Debug\net8.0\Cyaim.WebSocketServer.Sample.Dashboard.exe --node=node3
```

### 方法3: 使用环境变量

设置环境变量后直接运行：
```bash
# Windows PowerShell
$env:CLUSTER_NODE="node1"; .\bin\Debug\net8.0\Cyaim.WebSocketServer.Sample.Dashboard.exe

# Windows CMD
set CLUSTER_NODE=node1
.\bin\Debug\net8.0\Cyaim.WebSocketServer.Sample.Dashboard.exe
```

## 🌐 访问地址

启动后，可以通过以下地址访问各节点：

### HTTP API
- 节点1: http://localhost:5001
- 节点2: http://localhost:5002
- 节点3: http://localhost:5003

### Dashboard
- 节点1: http://localhost:5001/dashboard
- 节点2: http://localhost:5002/dashboard
- 节点3: http://localhost:5003/dashboard

### WebSocket 连接
- 节点1: ws://localhost:5001/ws
- 节点2: ws://localhost:5002/ws
- 节点3: ws://localhost:5003/ws

### Swagger API 文档
- 节点1: http://localhost:5001/swagger
- 节点2: http://localhost:5002/swagger
- 节点3: http://localhost:5003/swagger

## 🧪 测试集群功能

### 1. 查看集群状态

访问任意节点的 Dashboard：
```
http://localhost:5001/dashboard
```

在 Dashboard 中可以查看：
- 集群概览
- 节点列表和状态
- 客户端连接信息
- 消息路由表

### 2. 使用测试 API

使用 Swagger 或 curl 测试集群 API：

```bash
# 查看集群概览
curl http://localhost:5001/api/dashboard/cluster/overview

# 查看所有节点
curl http://localhost:5001/api/dashboard/cluster/nodes

# 查看集群健康状态
curl http://localhost:5001/api/dashboard/cluster/health
```

### 3. 测试跨节点消息

1. 连接到节点1的 WebSocket：`ws://localhost:5001/ws`
2. 连接到节点2的 WebSocket：`ws://localhost:5002/ws`
3. 通过节点1发送消息到节点2的连接
4. 在 Dashboard 中查看消息路由情况

## ⚙️ 配置说明

### 节点配置结构

```json
{
  "Cluster": {
    "NodeId": "node1",              // 当前节点ID
    "NodeAddress": "localhost",      // 节点地址
    "NodePort": 5001,               // 节点端口
    "TransportType": "ws",          // 传输类型（ws/redis/rabbitmq）
    "ChannelName": "/cluster",      // 集群通道名称
    "Nodes": [                      // 其他节点列表
      "ws://localhost:5002/node2",
      "ws://localhost:5003/node3"
    ]
  }
}
```

### 修改配置

如果需要修改节点配置：

1. 编辑对应的 `appsettings.nodeX.json` 文件
2. 确保节点ID和端口唯一
3. 确保 `Nodes` 数组中包含其他所有节点的地址
4. 重启对应的节点实例

## 🔍 故障排查

### 节点无法启动

1. 检查端口是否被占用
2. 检查配置文件是否存在且格式正确
3. 查看控制台日志输出

### 节点无法连接

1. 确保所有节点都已启动
2. 检查防火墙设置
3. 验证节点地址和端口配置是否正确
4. 查看日志中的连接错误信息

### 集群选举失败

1. 确保至少3个节点运行（Raft 协议要求）
2. 检查节点之间的网络连通性
3. 查看各节点的日志输出

## 📝 注意事项

1. **启动顺序**：建议按顺序启动节点（node1 -> node2 -> node3），但非必需
2. **端口冲突**：确保端口 5001、5002、5003 未被其他程序占用
3. **配置文件**：每个节点必须使用对应的配置文件（通过 `--node` 参数指定）
4. **集群大小**：Raft 协议建议使用奇数个节点（3、5、7等）以获得最佳容错性

## 🎯 下一步

- 查看 Dashboard 中的集群监控数据
- 测试跨节点的消息转发
- 测试节点故障恢复
- 查看集群选举过程

