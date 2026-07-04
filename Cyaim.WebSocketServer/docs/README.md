# Cyaim.WebSocketServer Documentation Center

Welcome to the Cyaim.WebSocketServer documentation center. This documentation provides a complete guide to using the library, organized by modules, to help you quickly understand and use various features.

## 🌐 Language / 语言

- **[English Documentation](./en/README.md)** - English documentation
- **[中文文档](./zh-cn/README.md)** - 中文文档

---

## 📚 Documentation Index

### Core Documentation

- **[Quick Start](./en/QUICK_START.md)** - Get started in 5 minutes
- **[Core Library](./en/CORE.md)** - Core features, routing system, handlers
- **[Configuration Guide](./en/CONFIGURATION.md)** - Detailed configuration options and best practices
- **[API Reference](./en/API_REFERENCE.md)** - Complete API documentation

### Feature Modules

- **[Cluster Module](./en/CLUSTER.md)** - Multi-node cluster, Raft protocol, message routing
- **[Cluster Transports](./en/CLUSTER_TRANSPORTS.md)** - Redis, RabbitMQ transport implementations
- **[Metrics](./en/METRICS.md)** - OpenTelemetry integration, performance monitoring
- **[Dashboard](./en/DASHBOARD.md)** - Monitoring panel, API interfaces, frontend UI
- **[Clients](./en/CLIENTS.md)** - Multi-language client SDKs with automatic endpoint discovery

## 🚀 Quick Navigation

### I'm a beginner, where do I start?

1. Read [Quick Start](./en/QUICK_START.md) to understand basic concepts
2. Check [Core Library](./en/CORE.md) to learn basic features
3. Refer to sample projects for practical applications

### I need to implement cluster functionality

1. Read [Cluster Module](./en/CLUSTER.md) to understand cluster architecture
2. Choose transport: WebSocket, Redis, or RabbitMQ
3. Check [Cluster Transports](./en/CLUSTER_TRANSPORTS.md) for extension packages

### I need monitoring and statistics

1. Read [Metrics](./en/METRICS.md) to understand monitoring features
2. Check [Dashboard](./en/DASHBOARD.md) for visualization interface
3. Configure OpenTelemetry to export metrics

### I need to understand configuration options

1. Read [Configuration Guide](./en/CONFIGURATION.md) for all configurations
2. Refer to [API Reference](./en/API_REFERENCE.md) for detailed parameters
3. Check configuration files in sample projects

### I need to use the client SDK

1. Read [Clients](./en/CLIENTS.md) to understand available client SDKs
2. Choose your preferred language (C#, TypeScript, Rust, Java, Dart, Python)
3. Follow the quick start guide for your language

## 📦 Project Structure

```
Cyaim.WebSocketServer/
├── Cyaim.WebSocketServer/              # Core library
│   ├── Infrastructure/
│   │   ├── Cluster/                    # Cluster functionality
│   │   ├── Handlers/                   # Handlers
│   │   ├── Configures/                 # Configuration
│   │   └── Metrics/                    # Metrics
│   ├── Middlewares/                    # Middlewares
│   └── ...
├── Cyaim.WebSocketServer.Dashboard/    # Dashboard backend
├── Cyaim.WebSocketServer.OpenTelemetry/ # Optional OTLP metrics exporter helper
├── Cyaim.WebSocketServer.Cluster.*/   # Cluster transport extensions
├── Clients/                            # Multi-language client SDKs
│   ├── Cyaim.WebSocketServer.Client/  # C# client
│   ├── cyaim-websocket-client-js/    # TypeScript/JavaScript client
│   ├── cyaim-websocket-client-rs/    # Rust client
│   ├── cyaim-websocket-client-java/  # Java client
│   ├── cyaim-websocket-client-dart/  # Dart client
│   └── cyaim-websocket-client-python/ # Python client
├── Sample/                             # Sample projects
└── docs/                               # Documentation directory
    ├── en/                             # English documentation
    └── zh-cn/                          # Chinese documentation
```

## 🎯 Features

### Core Features

- ✅ **Lightweight & High Performance** - Based on ASP.NET Core, excellent performance
- ✅ **Routing System** - MVC-like routing mechanism, supports RESTful API
- ✅ **Full Duplex Communication** - Supports bidirectional communication
- ✅ **Multiplexing** - Single connection supports multiple requests/responses
- ✅ **Pipeline Processing** - Supports middleware pipeline pattern

### Cluster Features

- ✅ **Multi-node Cluster** - Supports horizontal scaling
- ✅ **Raft Protocol** - Raft-based consensus protocol
- ✅ **Auto Routing** - Automatic cross-node message routing
- ✅ **Fault Tolerance** - Automatic node failure handling
- ✅ **Graceful Shutdown** - Supports connection migration and graceful shutdown

### Monitoring Features

- ✅ **Real-time Statistics** - Connection count, message count, bandwidth, etc.
- ✅ **OpenTelemetry** - Standard metrics export
- ✅ **Dashboard** - Visual monitoring interface
- ✅ **Performance Analysis** - Detailed performance metrics

### Client SDK Features

- ✅ **Multi-language Support** - C#, TypeScript, Rust, Java, Dart, Python
- ✅ **Automatic Endpoint Discovery** - Auto-fetch endpoints from server
- ✅ **Interface Contract** - Type-safe interface-based calling
- ✅ **Flexible Configuration** - Lazy loading, validation options

## 🔧 Supported .NET Versions

- .NET Standard 2.1
- .NET 6.0
- .NET 7.0
- .NET 8.0
- .NET 9.0

## 📝 Version Information

Current Version: **1.7.8**

Check [CHANGELOG.md](../CHANGELOG.md) for version history.

## 🤝 Contributing

Contributions are welcome! Please check [CONTRIBUTING.md](../CONTRIBUTING.md) for contribution guidelines.

## 📄 License

This project is licensed under [LICENSE](../LICENSE).

## 🔗 Related Links

- [GitHub Repository](https://github.com/Cyaim/WebSocketServer)
- [NuGet Package](https://www.nuget.org/packages/Cyaim.WebSocketServer)
- [Issue Tracker](https://github.com/Cyaim/WebSocketServer/issues)

## 💡 Get Help

- Check the [Troubleshooting](./en/TROUBLESHOOTING.md) section in the documentation
- Submit an [Issue](https://github.com/Cyaim/WebSocketServer/issues) on GitHub
- Check sample projects for practical usage

---

**Last Updated**: 2024-12-XX

## 📦 Client SDKs

We now provide multi-language client SDKs for easy integration:

- **C#** - Full .NET support with dynamic proxy
- **TypeScript/JavaScript** - Type-safe client for web and Node.js
- **Rust** - High-performance async client
- **Java** - Maven package for Java applications
- **Dart** - Flutter and Dart support
- **Python** - Async client for Python 3.8+

See [Clients Documentation](./en/CLIENTS.md) for details.
