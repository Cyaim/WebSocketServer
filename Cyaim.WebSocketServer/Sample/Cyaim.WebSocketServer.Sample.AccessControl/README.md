# Access Control Test Sample / 访问控制测试示例

This sample project demonstrates how to use the Access Control feature of Cyaim.WebSocketServer.

本示例项目演示如何使用 Cyaim.WebSocketServer 的访问控制功能。

## Features / 功能特性

- ✅ IP Whitelist/Blacklist / IP 白名单/黑名单
- ✅ Country-based Filtering / 基于国家的过滤
- ✅ City-based Filtering / 基于城市的过滤
- ✅ Region-based Filtering / 基于地区的过滤
- ✅ Geographic Location Lookup / 地理位置查询

## Quick Start / 快速开始

### 1. Run the Server / 运行服务器

```bash
cd Cyaim.WebSocketServer.Sample.AccessControl
dotnet run
```

The server will start on `http://localhost:5000` and WebSocket endpoint at `ws://localhost:5000/ws`.

服务器将在 `http://localhost:5000` 启动，WebSocket 端点为 `ws://localhost:5000/ws`。

### 2. Test Connection / 测试连接

#### Test with Allowed IP / 使用允许的 IP 测试

If your IP is in the whitelist (e.g., `127.0.0.1`), the connection should succeed.

如果您的 IP 在白名单中（例如 `127.0.0.1`），连接应该成功。

#### Test with Blocked IP / 使用被阻止的 IP 测试

If your IP is not in the whitelist, the connection will be denied.

如果您的 IP 不在白名单中，连接将被拒绝。

## Configuration Examples / 配置示例

### Example 1: IP Whitelist Only / 仅 IP 白名单

Edit `Program.cs`:

```csharp
builder.Services.AddAccessControl(policy =>
{
    policy.Enabled = true;
    policy.IpWhitelist = new List<string>
    {
        "127.0.0.1",
        "192.168.1.0/24"
    };
    policy.EnableGeoLocationLookup = false;
});
```

### Example 2: Country-based Filtering / 基于国家的过滤

Edit `Program.cs`:

```csharp
builder.Services.AddAccessControl(policy =>
{
    policy.Enabled = true;
    policy.CountryWhitelist = new List<string> { "CN", "US" };
    policy.EnableGeoLocationLookup = true;
});

builder.Services.AddGeoLocationProvider<DefaultGeoLocationProvider>();
```

### Example 3: Configuration from appsettings.json / 从 appsettings.json 配置

Edit `appsettings.json`:

```json
{
  "AccessControlPolicy": {
    "Enabled": true,
    "IpWhitelist": ["127.0.0.1", "192.168.1.0/24"],
    "EnableGeoLocationLookup": false
  }
}
```

Edit `Program.cs`:

```csharp
builder.Services.AddAccessControl(builder.Configuration, "AccessControlPolicy");
```

## Testing / 测试

### Test with WebSocket Client / 使用 WebSocket 客户端测试

You can use any WebSocket client to test:

可以使用任何 WebSocket 客户端进行测试：

1. **Browser Console / 浏览器控制台**:
```javascript
const ws = new WebSocket('ws://localhost:5000/ws');
ws.onopen = () => console.log('Connected');
ws.onerror = (error) => console.log('Error:', error);
ws.onclose = () => console.log('Closed');
```

2. **PowerShell / PowerShell**:
```powershell
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$uri = New-Object System.Uri("ws://localhost:5000/ws")
$cancellationToken = New-Object System.Threading.CancellationToken
$ws.ConnectAsync($uri, $cancellationToken).Wait()
```

3. **wscat (Node.js tool) / wscat (Node.js 工具)**:
```bash
npm install -g wscat
wscat -c ws://localhost:5000/ws
```

### Test Scenarios / 测试场景

#### Scenario 1: IP Whitelist / IP 白名单

1. Configure whitelist with `127.0.0.1` / 配置白名单包含 `127.0.0.1`
2. Connect from `127.0.0.1` - Should succeed / 从 `127.0.0.1` 连接 - 应该成功
3. Connect from `192.168.1.100` (if not in whitelist) - Should fail / 从 `192.168.1.100` 连接（如果不在白名单中）- 应该失败

#### Scenario 2: Country Blacklist / 国家黑名单

1. Configure blacklist with country code `XX` / 配置黑名单包含国家代码 `XX`
2. Connect from IP in country `XX` - Should fail / 从国家 `XX` 的 IP 连接 - 应该失败
3. Connect from IP in other countries - Should succeed / 从其他国家的 IP 连接 - 应该成功

#### Scenario 3: City Whitelist / 城市白名单

1. Configure city whitelist with `CN:Beijing` / 配置城市白名单包含 `CN:Beijing`
2. Connect from Beijing - Should succeed / 从北京连接 - 应该成功
3. Connect from other cities - Should fail / 从其他城市连接 - 应该失败

## Logs / 日志

The server will log access control decisions:

服务器将记录访问控制决策：

- `Access denied for IP {ipAddress}` - Connection denied / 连接被拒绝
- `IP {ipAddress} is in whitelist, allowing access` - Connection allowed / 连接被允许
- `IP {ipAddress} is in blacklist, denying access` - Connection denied / 连接被拒绝

## Notes / 注意事项

1. **Local/Private IPs**: Local and private IPs (127.0.0.1, 192.168.x.x, 10.x.x.x) are skipped for geographic location lookup.

   **本地/私有 IP**：本地和私有 IP（127.0.0.1, 192.168.x.x, 10.x.x.x）会跳过地理位置查询。

2. **Rate Limits**: The free tier of ip-api.com has rate limits. For production use, consider using a commercial service.

   **速率限制**：ip-api.com 的免费版有速率限制。生产环境请考虑使用商业服务。

3. **Caching**: Geographic location results are cached to reduce API calls.

   **缓存**：地理位置查询结果会被缓存以减少 API 调用。

4. **CIDR Notation**: IP whitelist/blacklist supports CIDR notation (e.g., `192.168.1.0/24`).

   **CIDR 表示法**：IP 白名单/黑名单支持 CIDR 表示法（例如 `192.168.1.0/24`）。

