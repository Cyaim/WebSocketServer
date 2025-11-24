# WebSocketServer Dashboard

WebSocketServer Dashboard 是一个用于监控和管理 WebSocketServer 服务端（包含集群）的现代化仪表板应用。

## 功能特性 / Features

- 📊 **集群概览** / Cluster Overview: 查看所有节点状态、连接数、Raft 状态等
- 🖥️ **节点管理** / Node Management: 查看和管理集群节点
- 👥 **客户端管理** / Client Management: 查看所有客户端连接信息、统计信息
- 📈 **带宽监控** / Bandwidth Monitoring: 实时监控网络带宽使用情况
- 🔄 **数据流查看** / Data Flow Viewer: 查看实时数据流消息
- 📤 **消息发送** / Message Sender: 向指定连接发送测试消息
- 🎨 **现代化 UI** / Modern UI: 基于 Svelte 5 和 Tailwind CSS 构建的响应式界面
- 🌐 **国际化支持** / i18n Support: 支持中文和英文双语

## 技术栈 / Tech Stack

- **框架** / Framework: SvelteKit 2.x
- **UI 库** / UI Library: Svelte 5
- **样式** / Styling: Tailwind CSS 4.x
- **国际化** / i18n: Paraglide.js (inlang)
- **构建工具** / Build Tool: Vite 7.x
- **包管理** / Package Manager: pnpm

## 开发 / Development

### 安装依赖 / Install Dependencies

```bash
pnpm install
```

### 开发模式 / Development Mode

```bash
pnpm dev
```

访问 `http://localhost:5173` 查看应用。

### 构建生产版本 / Build for Production

```bash
pnpm build
```

构建后的文件将输出到 `build` 目录。

### 预览生产版本 / Preview Production Build

```bash
pnpm preview
```

## 项目结构 / Project Structure

```
src/
├── lib/
│   ├── api/          # API 客户端
│   ├── types/        # TypeScript 类型定义
│   └── paraglide/    # i18n 生成的代码
├── routes/
│   ├── dashboard/    # Dashboard 路由
│   │   ├── overview/ # 集群概览
│   │   ├── nodes/    # 节点管理
│   │   ├── clients/  # 客户端列表
│   │   ├── bandwidth/# 带宽监控
│   │   ├── dataflow/ # 数据流查看
│   │   └── send/     # 消息发送
│   └── +layout.svelte
messages/              # i18n 翻译文件
├── en.json           # 英文翻译
└── zh-cn.json        # 中文翻译
```

## API 端点 / API Endpoints

Dashboard 需要后端 API 支持，API 端点位于 `/api/dashboard`：

- `GET /api/dashboard/cluster/overview` - 获取集群概览
- `GET /api/dashboard/cluster/nodes` - 获取节点列表
- `GET /api/dashboard/clients` - 获取客户端连接列表
- `GET /api/dashboard/bandwidth` - 获取带宽统计信息
- `POST /api/dashboard/send` - 发送消息到指定连接

## 配置 / Configuration

### API 基础 URL

默认 API 基础 URL 为 `/api/dashboard`，可以在 `src/lib/api/dashboard.ts` 中修改：

```typescript
const API_BASE_URL = '/api/dashboard';
```

### 国际化 / Internationalization

翻译文件位于 `messages/` 目录：
- `en.json` - 英文翻译
- `zh-cn.json` - 中文翻译

使用 Paraglide.js 进行国际化，翻译会自动生成到 `src/lib/paraglide/` 目录。

## 响应式设计 / Responsive Design

Dashboard 使用 Tailwind CSS 实现响应式布局：
- **移动端** / Mobile: 单列布局
- **平板** / Tablet: 2 列布局
- **桌面** / Desktop: 3-4 列布局

## 许可证 / License

Copyright © Cyaim Studio
