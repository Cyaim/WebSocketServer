# GitHub Actions 自动发布 NuGet 包说明

## 功能说明

当同时满足以下条件时，会自动触发发布：
1. 提交的 commit message 包含 `release version`
2. 提交者邮箱是 `lbhdr@outlook.com`

满足条件后会自动：
1. 生成版本号（格式：`主版本号.年后两位.月日.时分`，例如：`2.25.1225.1430`）
2. 更新所有可发布项目的版本号
3. 构建和打包所有项目
4. 发布到 NuGet
5. 创建 GitHub Release

## 版本号格式

- **格式**：`主版本号.年后两位.月日.时分`
- **当前主版本号**：`2`
- **示例**：
  - `2.25.1225.1430` = Version 2, Year 2025, Dec 25, 14:30 UTC
  - `2.25.0101.0900` = Version 2, Year 2025, Jan 01, 09:00 UTC

## 配置步骤

### 1. 设置 NuGet API Key

1. 访问 [NuGet.org](https://www.nuget.org/)
2. 登录你的账户
3. 点击右上角用户名 → **API Keys**
4. 创建新的 API Key 或使用现有的
5. 复制 API Key

### 2. 在 GitHub 仓库中设置 Secret

1. 打开你的 GitHub 仓库
2. 进入 **Settings** → **Secrets and variables** → **Actions**
3. 点击 **New repository secret**
4. 名称：`NUGET_APIKEY`
5. 值：粘贴你的 NuGet API Key
6. 点击 **Add secret**

## 使用方法

### 触发发布

在提交代码时，需要同时满足以下条件才能触发自动发布：

1. **Commit message 包含 `release version`**（不区分大小写）
2. **提交者邮箱是 `lbhdr@outlook.com`**

示例：

```bash
# 确保 Git 配置的邮箱是 lbhdr@outlook.com
git config user.email "lbhdr@outlook.com"

# 提交代码（commit message 包含 release version）
git commit -m "release version: 更新功能说明"
git push origin main
```

或者：

```bash
git commit -m "fix: 修复bug release version"
git push origin main
```

**注意**：
- Commit message 中只要包含 `release version`（不区分大小写）即可
- 提交者邮箱必须是 `lbhdr@outlook.com`（区分大小写）
- 如果 commit message 包含 `release version` 但提交者邮箱不是 `lbhdr@outlook.com`，workflow 会跳过发布

### 发布流程

1. **检查 commit message**：workflow 会检查最后一个 commit 的 message
2. **生成版本号**：基于当前 UTC 时间生成版本号
3. **更新项目文件**：自动更新所有可发布项目的 `.csproj` 文件中的版本号
4. **构建和打包**：构建 Release 版本并生成 NuGet 包
5. **发布到 NuGet**：自动推送所有包到 NuGet.org
6. **创建 GitHub Release**：自动创建带标签的 GitHub Release

## 支持的目标框架

所有发布的包都支持以下目标框架：
- **.NET Standard 2.1**
- **.NET 6.0**
- **.NET 7.0**
- **.NET 8.0**
- **.NET 9.0**
- **.NET 10.0**

**说明**：.NET SDK 10.0 向后兼容，可以构建所有上述目标框架。workflow 会自动为每个目标框架生成对应的程序集并打包到 NuGet 包中。

## 会发布的项目

以下项目会被自动更新版本号并发布：

1. `Cyaim.WebSocketServer` - 主库
2. `Cyaim.WebSocketServer.MessagePack` - MessagePack 扩展
3. `Cyaim.WebSocketServer.Dashboard` - Dashboard 监控
4. `Cyaim.WebSocketServer.Cluster.StackExchangeRedis` - StackExchange.Redis 集群扩展
5. `Cyaim.WebSocketServer.Cluster.RabbitMQ` - RabbitMQ 集群扩展
6. `Cyaim.WebSocketServer.Cluster.FreeRedis` - FreeRedis 集群扩展
7. `Cyaim.WebSocketServer.Cluster.Hybrid` - 混合集群扩展
8. `Cyaim.WebSocketServer.Cluster.Hybrid.Implementations` - 混合集群实现

**注意**：`Sample` 和 `Tests` 目录下的项目不会被发布。

## 查看发布状态

1. 进入 GitHub 仓库的 **Actions** 标签页
2. 查看 `Release NuGet Packages` workflow 的运行状态
3. 如果成功，可以在：
   - **NuGet.org** 查看发布的包
   - **GitHub Releases** 查看创建的 Release

## 故障排除

### 发布失败

1. **检查 NuGet API Key**：确保 `NUGET_APIKEY` secret 已正确设置
2. **检查 commit message**：确保包含 `release version`（不区分大小写）
3. **检查提交者邮箱**：确保 Git 配置的邮箱是 `lbhdr@outlook.com`（区分大小写）
   ```bash
   # 查看当前 Git 邮箱
   git config user.email
   
   # 设置 Git 邮箱为 lbhdr@outlook.com
   git config user.email "lbhdr@outlook.com"
   
   # 全局设置（可选）
   git config --global user.email "lbhdr@outlook.com"
   ```
4. **查看 workflow 日志**：在 Actions 页面查看详细的错误信息和检查结果

### 版本号格式错误

如果版本号格式不符合预期，检查：
- 系统时间是否正确
- workflow 文件中的版本号生成逻辑

### 包已存在错误

如果包已存在，workflow 会使用 `--skip-duplicate` 参数跳过，不会报错。

## 自定义配置

### 修改主版本号

编辑 `.github/workflows/release-nuget.yml`，修改：

```yaml
MAJOR_VERSION=2  # 改为你需要的版本号
```

### 修改触发条件

编辑 `.github/workflows/release-nuget.yml`，修改 commit message 检查逻辑：

```yaml
if echo "$COMMIT_MSG" | grep -qi "release version"; then
  # 可以改为其他触发条件
fi
```

### 添加更多项目

在 `Update version in all project files` 步骤中添加项目路径：

```yaml
PROJECTS=(
  "Cyaim.WebSocketServer/YourNewProject/YourNewProject.csproj"
  # ... 其他项目
)
```

## 注意事项

⚠️ **重要提示**：

1. **版本号唯一性**：确保同一时间只触发一次发布，避免版本号冲突
2. **API Key 安全**：不要将 API Key 提交到代码仓库
3. **测试发布**：首次使用前，建议先在测试分支测试
4. **时间同步**：版本号基于 UTC 时间，确保服务器时间正确

