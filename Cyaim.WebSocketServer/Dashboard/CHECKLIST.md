# Dashboard 运行检查清单 / Running Checklist

## ✅ 后端配置检查 / Backend Configuration Checklist

- [ ] 已添加项目引用到 `Cyaim.WebSocketServer.Example.csproj`
- [ ] 已在 `Startup.cs` 的 `ConfigureServices` 中添加 `services.AddWebSocketDashboard()`
- [ ] 已在 `Startup.cs` 的 `Configure` 中添加 `app.UseWebSocketDashboard("/dashboard")`
- [ ] 已确保 `endpoints.MapControllers()` 已添加（Dashboard API 需要）
- [ ] 已确保 Dashboard 中间件在 `MapControllers` 之后调用
- [ ] 如果前后端分离运行，已配置 CORS

## ✅ 前端配置检查 / Frontend Configuration Checklist

- [ ] 已运行 `pnpm install` 安装依赖
- [ ] `vite.config.ts` 中已配置 API 代理（开发模式）
- [ ] 已运行 `pnpm prepare` 生成 i18n 代码（如果需要）

## 🚀 运行步骤 / Running Steps

### 开发模式

1. **终端 1 - 启动后端**：
   ```bash
   cd Cyaim.WebSocketServer.Example
   dotnet run
   ```
   等待显示：`Now listening on: http://localhost:5000`

2. **终端 2 - 启动前端**：
   ```bash
   cd Dashboard/websocketserver-dashboard
   pnpm dev
   ```
   等待显示：`Local: http://localhost:5173`

3. **浏览器访问**：
   ```
   http://localhost:5173/dashboard/overview
   ```

### 生产模式

1. **构建前端**：
   ```bash
   cd Dashboard/websocketserver-dashboard
   pnpm build
   ```

2. **复制文件**：
   ```bash
   # Windows
   New-Item -ItemType Directory -Force -Path "..\Cyaim.WebSocketServer.Dashboard\wwwroot\public"
   Copy-Item -Path "build\*" -Destination "..\Cyaim.WebSocketServer.Dashboard\wwwroot\public\" -Recurse -Force
   ```

3. **启动后端**：
   ```bash
   cd Cyaim.WebSocketServer.Example
   dotnet run
   ```

4. **浏览器访问**：
   ```
   http://localhost:5000/dashboard
   ```

## 🔍 验证 / Verification

### 检查后端 API 是否正常

访问：`http://localhost:5000/api/dashboard/cluster/overview`

应该返回 JSON 响应：
```json
{
  "success": true,
  "data": { ... }
}
```

### 检查前端是否正常

1. 打开浏览器开发者工具（F12）
2. 查看 Console 标签，应该没有错误
3. 查看 Network 标签，API 请求应该返回 200 状态码

## 🐛 故障排除 / Troubleshooting

### 问题：前端显示 "Dashboard API not available"

**解决方案**：
1. 检查后端是否正在运行
2. 检查 `vite.config.ts` 中的代理配置
3. 检查浏览器控制台的网络请求

### 问题：CORS 错误

**解决方案**：在 `Startup.cs` 中添加 CORS 配置（见 QUICK_START.md）

### 问题：404 错误

**解决方案**：
1. 检查中间件顺序
2. 检查路径配置
3. 如果使用生产模式，检查文件是否已复制

### 问题：i18n 翻译不工作

**解决方案**：
```bash
cd Dashboard/websocketserver-dashboard
pnpm prepare
```

