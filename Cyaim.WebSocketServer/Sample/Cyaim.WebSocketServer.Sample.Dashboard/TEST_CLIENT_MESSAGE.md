# 客户端消息发送测试指南 / Client Message Sending Test Guide

## 测试步骤 / Test Steps

### 1. 启动集群节点 / Start Cluster Nodes

启动至少 2 个节点（例如 node1 和 node2）

### 2. 建立 WebSocket 连接 / Establish WebSocket Connections

#### Client1 连接（连接到 node1）
```javascript
// 在浏览器控制台或 Node.js 中执行
const ws1 = new WebSocket('ws://localhost:5001/ws');
ws1.onopen = () => {
    console.log('Client1 connected, ConnectionId:', ws1.url);
};
ws1.onmessage = (event) => {
    console.log('Client1 received:', event.data);
};
```

#### Client2 连接（连接到 node2）
```javascript
const ws2 = new WebSocket('ws://localhost:5002/ws');
ws2.onopen = () => {
    console.log('Client2 connected');
};
ws2.onmessage = (event) => {
    console.log('Client2 received:', event.data);
};
```

### 3. 获取连接 ID / Get Connection IDs

#### 方法 1: 通过 API 查询 / Method 1: Query via API

```bash
# 获取所有连接
curl http://localhost:5001/ws_server/api/client

# 获取 node1 的连接
curl http://localhost:5001/ws_server/api/client?nodeId=node1

# 获取 node2 的连接
curl http://localhost:5002/ws_server/api/client?nodeId=node2
```

#### 方法 2: 从 WebSocket 对象获取 / Method 2: Get from WebSocket object

注意：浏览器 WebSocket API 不直接提供 ConnectionId，需要通过服务器日志或 API 查询获取。

### 4. 发送消息 / Send Message

#### 使用 curl 命令 / Using curl

```bash
# Client1 发送消息给 Client2
# 假设 Client2 的 ConnectionId 是 "0HNHDGIF4BNLV"

curl -X POST http://localhost:5001/ws_server/api/messages/send \
  -H "Content-Type: application/json" \
  -d '{
    "connectionId": "0HNHDGIF4BNLV",
    "content": "Hello from Client1!",
    "messageType": "Text"
  }'
```

#### 使用 PowerShell / Using PowerShell

```powershell
# Client1 发送消息给 Client2
$body = @{
    connectionId = "0HNHDGIF4BNLV"
    content = "Hello from Client1!"
    messageType = "Text"
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5001/ws_server/api/messages/send" `
    -Method POST `
    -ContentType "application/json" `
    -Body $body
```

#### 使用 Swagger UI / Using Swagger UI

1. 打开 `http://localhost:5001/swagger`
2. 找到 `/ws_server/api/messages/send` 接口
3. 点击 "Try it out"
4. 输入参数：
   - `connectionId`: Client2 的连接 ID
   - `content`: 消息内容
   - `messageType`: "Text" 或 "Binary"
5. 点击 "Execute"

### 5. 验证消息接收 / Verify Message Reception

检查 Client2 的 WebSocket 是否收到消息：

```javascript
// 在 Client2 的浏览器控制台中
ws2.onmessage = (event) => {
    console.log('Client2 received message:', event.data);
    alert('Received: ' + event.data);
};
```

## 测试场景 / Test Scenarios

### 场景 1: 同节点内消息发送 / Scenario 1: Same Node Message Sending
- Client1 和 Client2 都连接到 node1
- 从 node1 发送消息给 Client2

### 场景 2: 跨节点消息发送 / Scenario 2: Cross-Node Message Sending
- Client1 连接到 node1
- Client2 连接到 node2
- 从 node1 发送消息给 Client2（通过集群路由）

### 场景 3: 广播消息 / Scenario 3: Broadcast Message

```bash
# 广播消息给所有连接
curl -X POST http://localhost:5001/ws_server/api/messages/broadcast \
  -H "Content-Type: application/json" \
  -d '{
    "content": "Broadcast message to all clients!",
    "messageType": "Text"
  }'
```

## 完整测试脚本 / Complete Test Script

### HTML 测试页面 / HTML Test Page

创建一个 `test-client-message.html` 文件：

```html
<!DOCTYPE html>
<html>
<head>
    <title>WebSocket Client Message Test</title>
</head>
<body>
    <h1>WebSocket 客户端消息测试</h1>
    
    <div>
        <h2>Client1 (连接到 node1)</h2>
        <button onclick="connectClient1()">连接 Client1</button>
        <button onclick="disconnectClient1()">断开 Client1</button>
        <div id="client1-status">未连接</div>
        <div id="client1-messages"></div>
    </div>
    
    <div>
        <h2>Client2 (连接到 node2)</h2>
        <button onclick="connectClient2()">连接 Client2</button>
        <button onclick="disconnectClient2()">断开 Client2</button>
        <div id="client2-status">未连接</div>
        <div id="client2-messages"></div>
    </div>
    
    <div>
        <h2>发送消息</h2>
        <input type="text" id="target-connection-id" placeholder="目标连接ID">
        <input type="text" id="message-content" placeholder="消息内容">
        <button onclick="sendMessage()">发送消息</button>
    </div>
    
    <script>
        let ws1 = null;
        let ws2 = null;
        let client1Id = null;
        let client2Id = null;
        
        function connectClient1() {
            ws1 = new WebSocket('ws://localhost:5001/ws');
            ws1.onopen = () => {
                document.getElementById('client1-status').textContent = '已连接';
                fetchClientIds();
            };
            ws1.onmessage = (event) => {
                const div = document.getElementById('client1-messages');
                div.innerHTML += '<p>收到: ' + event.data + '</p>';
            };
            ws1.onerror = (error) => {
                console.error('Client1 error:', error);
            };
        }
        
        function connectClient2() {
            ws2 = new WebSocket('ws://localhost:5002/ws');
            ws2.onopen = () => {
                document.getElementById('client2-status').textContent = '已连接';
                fetchClientIds();
            };
            ws2.onmessage = (event) => {
                const div = document.getElementById('client2-messages');
                div.innerHTML += '<p>收到: ' + event.data + '</p>';
            };
            ws2.onerror = (error) => {
                console.error('Client2 error:', error);
            };
        }
        
        function disconnectClient1() {
            if (ws1) {
                ws1.close();
                ws1 = null;
                document.getElementById('client1-status').textContent = '已断开';
            }
        }
        
        function disconnectClient2() {
            if (ws2) {
                ws2.close();
                ws2 = null;
                document.getElementById('client2-status').textContent = '已断开';
            }
        }
        
        async function fetchClientIds() {
            try {
                const response = await fetch('http://localhost:5001/ws_server/api/client');
                const data = await response.json();
                if (data.success && data.data) {
                    // 假设第一个连接是 Client1，第二个是 Client2
                    if (data.data.length > 0) {
                        client1Id = data.data[0].connectionId;
                        document.getElementById('target-connection-id').placeholder = '当前可用: ' + client1Id;
                    }
                    if (data.data.length > 1) {
                        client2Id = data.data[1].connectionId;
                    }
                }
            } catch (error) {
                console.error('Failed to fetch client IDs:', error);
            }
        }
        
        async function sendMessage() {
            const connectionId = document.getElementById('target-connection-id').value || client2Id;
            const content = document.getElementById('message-content').value;
            
            if (!connectionId || !content) {
                alert('请填写连接ID和消息内容');
                return;
            }
            
            try {
                const response = await fetch('http://localhost:5001/ws_server/api/messages/send', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({
                        connectionId: connectionId,
                        content: content,
                        messageType: 'Text'
                    })
                });
                
                const result = await response.json();
                if (result.success) {
                    alert('消息发送成功！');
                } else {
                    alert('消息发送失败: ' + result.error);
                }
            } catch (error) {
                console.error('Error sending message:', error);
                alert('发送消息时出错: ' + error.message);
            }
        }
    </script>
</body>
</html>
```

## 注意事项 / Notes

1. **连接 ID 获取**：连接建立后，需要通过 API 查询获取 ConnectionId
2. **跨节点路由**：如果 Client1 和 Client2 在不同节点，消息会自动通过集群路由
3. **消息格式**：当前发送的是纯文本消息，如果需要发送 JSON，需要在 content 中包含 JSON 字符串
4. **错误处理**：如果连接不存在或已断开，API 会返回相应的错误信息

