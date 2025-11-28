# Cyaim.WebSocketServer 文档中心

欢迎使用 Cyaim.WebSocketServer 文档中心。本文档提供了完整的库使用指南，按模块组织，帮助您快速了解和使用各个功能。

## 📚 文档索引

### 核心文档

- **[快速开始](./QUICK_START.md)** - 5 分钟快速上手
- **[核心库文档](./CORE.md)** - 核心功能、路由系统、处理器详解
- **[配置指南](./CONFIGURATION.md)** - 详细配置选项和最佳实践
- **[API 参考](./API_REFERENCE.md)** - 完整的 API 文档

### 功能模块

- **[集群模块](./CLUSTER.md)** - 多节点集群、Raft 协议、消息路由
- **[集群传输扩展](./CLUSTER_TRANSPORTS.md)** - Redis、RabbitMQ 传输实现
- **[指标统计](./METRICS.md)** - OpenTelemetry 集成、性能监控
- **[Dashboard](./DASHBOARD.md)** - 监控面板、API 接口、前端界面
- **[客户端 SDK](./CLIENTS.md)** - 多语言客户端 SDK，支持自动 endpoint 发现

## 🚀 快速导航

### 我是新手，从哪里开始？

1. 阅读 [快速开始](./QUICK_START.md) 了解基本概念
2. 查看 [核心库文档](./CORE.md) 学习基础功能
3. 参考示例项目了解实际应用

### 我需要实现集群功能

1. 阅读 [集群模块](./CLUSTER.md) 了解集群架构
2. 选择传输方式：WebSocket、Redis 或 RabbitMQ
3. 查看 [集群传输扩展](./CLUSTER_TRANSPORTS.md) 了解扩展包

### 我需要监控和统计

1. 阅读 [指标统计](./METRICS.md) 了解监控功能
2. 查看 [Dashboard](./DASHBOARD.md) 了解可视化界面
3. 配置 OpenTelemetry 导出指标

### 我需要了解配置选项

1. 阅读 [配置指南](./CONFIGURATION.md) 查看所有配置
2. 参考 [API 参考](./API_REFERENCE.md) 了解详细参数
3. 查看示例项目中的配置文件

### 我需要使用客户端 SDK

1. 阅读 [客户端 SDK](./CLIENTS.md) 了解可用的客户端 SDK
2. 选择您偏好的语言（C#、TypeScript、Rust、Java、Dart、Python）
3. 按照对应语言的快速开始指南

## 📦 项目结构

```
Cyaim.WebSocketServer/
├── Cyaim.WebSocketServer/              # 核心库
│   ├── Infrastructure/
│   │   ├── Cluster/                    # 集群功能
│   │   ├── Handlers/                   # 处理器
│   │   ├── Configures/                 # 配置
│   │   └── Metrics/                     # 指标统计
│   ├── Middlewares/                    # 中间件
│   └── ...
├── Cyaim.WebSocketServer.Dashboard/    # Dashboard 后端
├── Cyaim.WebSocketServer.Cluster.*/   # 集群传输扩展
├── Clients/                            # 多语言客户端 SDK
│   ├── Cyaim.WebSocketServer.Client/  # C# 客户端
│   ├── cyaim-websocket-client-js/    # TypeScript/JavaScript 客户端
│   ├── cyaim-websocket-client-rs/    # Rust 客户端
│   ├── cyaim-websocket-client-java/  # Java 客户端
│   ├── cyaim-websocket-client-dart/  # Dart 客户端
│   └── cyaim-websocket-client-python/ # Python 客户端
├── Sample/                             # 示例项目
└── docs/                               # 文档目录
    ├── en/                             # 英文文档
    └── zh-cn/                          # 中文文档
```

## 🎯 功能特性

### 核心功能

- ✅ **轻量级高性能** - 基于 ASP.NET Core，性能优异
- ✅ **路由系统** - 类似 MVC 的路由机制，支持 RESTful API
- ✅ **全双工通信** - 支持客户端和服务器双向通信
- ✅ **多路复用** - 单个连接支持多个请求/响应
- ✅ **管道处理** - 支持中间件管道模式

### 集群功能

- ✅ **多节点集群** - 支持水平扩展
- ✅ **Raft 协议** - 基于 Raft 的一致性协议
- ✅ **自动路由** - 跨节点消息自动路由
- ✅ **故障转移** - 节点故障自动处理
- ✅ **优雅关闭** - 支持连接迁移和优雅关闭

### 监控功能

- ✅ **实时统计** - 连接数、消息数、带宽等
- ✅ **OpenTelemetry** - 标准指标导出
- ✅ **Dashboard** - 可视化监控界面
- ✅ **性能分析** - 详细的性能指标

### 客户端 SDK 功能

- ✅ **多语言支持** - C#、TypeScript、Rust、Java、Dart、Python
- ✅ **自动 Endpoint 发现** - 自动从服务器获取 endpoint 列表
- ✅ **接口契约式调用** - 类型安全的接口调用
- ✅ **灵活配置** - 延迟加载、验证选项

## 🔧 支持的 .NET 版本

- .NET Standard 2.1
- .NET 6.0
- .NET 7.0
- .NET 8.0
- .NET 9.0

## 📝 版本信息

当前版本：**1.7.8**

查看 [CHANGELOG.md](../../CHANGELOG.md) 了解版本历史。

## 🤝 贡献

欢迎贡献代码和文档！请查看 [CONTRIBUTING.md](../../CONTRIBUTING.md) 了解贡献指南。

## 📄 许可证

本项目采用 [LICENSE](../../LICENSE) 许可证。

## 🔗 相关链接

- [GitHub 仓库](https://github.com/Cyaim/WebSocketServer)
- [NuGet 包](https://www.nuget.org/packages/Cyaim.WebSocketServer)
- [问题反馈](https://github.com/Cyaim/WebSocketServer/issues)

## 💡 获取帮助

- 查看文档中的故障排除章节
- 在 GitHub 上提交 [Issue](https://github.com/Cyaim/WebSocketServer/issues)
- 查看示例项目了解实际用法

---

**最后更新**: 2024-12-XX

## 📦 客户端 SDK

我们现在提供多语言客户端 SDK，方便集成：

- **C#** - 完整的 .NET 支持，带动态代理
- **TypeScript/JavaScript** - 适用于 Web 和 Node.js 的类型安全客户端
- **Rust** - 高性能异步客户端
- **Java** - 适用于 Java 应用的 Maven 包
- **Dart** - Flutter 和 Dart 支持
- **Python** - 适用于 Python 3.8+ 的异步客户端

查看 [客户端文档](./CLIENTS.md) 了解详情。

