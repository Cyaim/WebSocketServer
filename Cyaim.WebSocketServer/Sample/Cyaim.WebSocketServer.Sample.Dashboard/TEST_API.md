# WebSocket 集群测试 API 文档

## 概述

本项目提供了完整的 WebSocket 集群测试功能，包括 RESTful API 端点和自动化测试服务。

## API 端点

所有测试 API 的基础路径为：`/api/test/cluster`

### 1. 集群信息测试

#### 获取集群概览
```
GET /api/test/cluster/overview
```

#### 获取所有节点
```
GET /api/test/cluster/nodes
```

#### 获取指定节点信息
```
GET /api/test/cluster/nodes/{nodeId}
```

#### 检查是否为领导者
```
GET /api/test/cluster/leader
```

#### 获取当前节点ID
```
GET /api/test/cluster/current-node
```

#### 获取最优节点
```
GET /api/test/cluster/optimal-node
```

### 2. 客户端连接测试

#### 获取所有客户端
```
GET /api/test/cluster/clients?nodeId={nodeId}
```
参数：
- `nodeId` (可选): 节点ID过滤器

#### 获取指定节点的客户端
```
GET /api/test/cluster/clients/node/{nodeId}
```

#### 获取本地客户端
```
GET /api/test/cluster/clients/local
```

#### 获取指定连接信息
```
GET /api/test/cluster/clients/{connectionId}
```

#### 获取连接数统计
```
GET /api/test/cluster/connections/count
```

### 3. 消息发送测试

#### 发送文本消息
```
POST /api/test/cluster/send/text
Content-Type: application/json

{
  "connectionId": "connection-id",
  "content": "Hello, World!"
}
```

#### 发送二进制消息
```
POST /api/test/cluster/send/binary
Content-Type: application/json

{
  "connectionId": "connection-id",
  "content": "base64-encoded-content"
}
```

#### 广播消息
```
POST /api/test/cluster/broadcast
Content-Type: application/json

{
  "content": "Broadcast message",
  "messageType": "Text"
}
```

#### 向指定节点广播消息
```
POST /api/test/cluster/broadcast/node/{nodeId}
Content-Type: application/json

{
  "content": "Node broadcast message",
  "messageType": "Text"
}
```

### 4. 统计信息测试

#### 获取带宽统计
```
GET /api/test/cluster/statistics/bandwidth
```

#### 获取连接统计
```
GET /api/test/cluster/statistics/connections
```

### 5. 路由测试

#### 获取连接路由表
```
GET /api/test/cluster/routes
```

#### 查询连接所在节点
```
GET /api/test/cluster/routes/{connectionId}
```

### 6. 健康检查测试

#### 集群健康检查
```
GET /api/test/cluster/health
```

#### 节点健康检查
```
GET /api/test/cluster/health/node/{nodeId}
```

### 7. 综合测试

#### 运行所有测试
```
POST /api/test/cluster/run-all
```

#### 运行集群信息测试
```
POST /api/test/cluster/run/cluster-info
```

#### 运行连接管理测试
```
POST /api/test/cluster/run/connections
```

#### 运行消息发送测试
```
POST /api/test/cluster/run/messages
```

## 使用示例

### 使用 curl 测试

```bash
# 获取集群概览
curl http://localhost:5000/api/test/cluster/overview

# 获取所有客户端
curl http://localhost:5000/api/test/cluster/clients

# 发送文本消息
curl -X POST http://localhost:5000/api/test/cluster/send/text \
  -H "Content-Type: application/json" \
  -d '{"connectionId":"your-connection-id","content":"Hello"}'

# 运行所有测试
curl -X POST http://localhost:5000/api/test/cluster/run-all
```

### 使用 C# HttpClient 测试

```csharp
using var httpClient = new HttpClient();
httpClient.BaseAddress = new Uri("http://localhost:5000");

// 获取集群概览
var response = await httpClient.GetAsync("/api/test/cluster/overview");
var content = await response.Content.ReadAsStringAsync();
Console.WriteLine(content);

// 运行所有测试
var testResponse = await httpClient.PostAsync("/api/test/cluster/run-all", null);
var testContent = await testResponse.Content.ReadAsStringAsync();
Console.WriteLine(testContent);
```

### 使用 Postman 或类似工具

1. 导入以下集合：
   - 基础 URL: `http://localhost:5000`
   - 所有端点都在 `/api/test/cluster` 路径下

2. 测试流程：
   - 首先运行 `GET /api/test/cluster/overview` 查看集群状态
   - 运行 `GET /api/test/cluster/clients` 查看所有连接
   - 选择一个连接ID，使用 `POST /api/test/cluster/send/text` 发送消息
   - 运行 `POST /api/test/cluster/run-all` 执行完整测试套件

## 测试结果格式

所有测试端点返回统一的 `ApiResponse<T>` 格式：

```json
{
  "success": true,
  "data": { ... },
  "error": null,
  "timestamp": "2024-01-01T00:00:00Z"
}
```

综合测试返回 `TestResults` 格式：

```json
{
  "success": true,
  "data": {
    "testName": "All Tests",
    "success": true,
    "startTime": "2024-01-01T00:00:00Z",
    "endTime": "2024-01-01T00:00:01Z",
    "duration": "00:00:01",
    "passedCount": 10,
    "failedCount": 0,
    "errorMessage": null,
    "tests": [
      {
        "name": "GetClusterOverview",
        "success": true,
        "startTime": "2024-01-01T00:00:00Z",
        "endTime": "2024-01-01T00:00:00.100Z",
        "duration": "00:00:00.100",
        "errorMessage": null
      }
    ]
  }
}
```

## 测试场景

### 场景1：验证集群基本功能

```bash
# 1. 检查集群状态
curl http://localhost:5000/api/test/cluster/overview

# 2. 检查节点列表
curl http://localhost:5000/api/test/cluster/nodes

# 3. 检查连接数
curl http://localhost:5000/api/test/cluster/connections/count
```

### 场景2：测试消息发送

```bash
# 1. 获取所有连接
curl http://localhost:5000/api/test/cluster/clients

# 2. 选择一个连接ID，发送消息
curl -X POST http://localhost:5000/api/test/cluster/send/text \
  -H "Content-Type: application/json" \
  -d '{"connectionId":"connection-id-here","content":"Test message"}'

# 3. 广播消息
curl -X POST http://localhost:5000/api/test/cluster/broadcast \
  -H "Content-Type: application/json" \
  -d '{"content":"Broadcast test","messageType":"Text"}'
```

### 场景3：运行完整测试套件

```bash
# 运行所有测试
curl -X POST http://localhost:5000/api/test/cluster/run-all

# 或运行特定测试组
curl -X POST http://localhost:5000/api/test/cluster/run/cluster-info
curl -X POST http://localhost:5000/api/test/cluster/run/connections
curl -X POST http://localhost:5000/api/test/cluster/run/messages
```

## 注意事项

1. **端口配置**: 默认端口为 5000，如果使用其他端口，请相应修改 URL
2. **连接要求**: 某些测试（如消息发送）需要存在活跃的 WebSocket 连接
3. **集群配置**: 确保 WebSocket 集群已正确配置和启动
4. **错误处理**: 所有 API 都包含错误处理，检查返回的 `success` 字段

## 集成到 CI/CD

可以将这些测试端点集成到 CI/CD 流程中：

```yaml
# GitHub Actions 示例
- name: Run Cluster Tests
  run: |
    curl -X POST http://localhost:5000/api/test/cluster/run-all \
      -H "Content-Type: application/json" \
      -o test-results.json
    
    # 检查测试结果
    if ! jq '.data.success' test-results.json; then
      echo "Tests failed"
      exit 1
    fi
```

## 许可证

Copyright © Cyaim Studio

