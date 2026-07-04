# Cyaim.WebSocketServer Documentation Center

Welcome to the Cyaim.WebSocketServer documentation center. This documentation provides a complete guide to using the library, organized by modules, to help you quickly understand and use various features.

## 📚 Documentation Index

### Core Documentation

- **[Quick Start](./QUICK_START.md)** - Get started in 5 minutes
- **[Core Library](./CORE.md)** - Core features, routing system, handlers
- **[Configuration Guide](./CONFIGURATION.md)** - Detailed configuration options and best practices
- **[API Reference](./API_REFERENCE.md)** - Complete API documentation

### Feature Modules

- **[Cluster Module](./CLUSTER.md)** - Multi-node cluster, Raft protocol, message routing
- **[Cluster Transports](./CLUSTER_TRANSPORTS.md)** - Redis, RabbitMQ transport implementations
- **[Hybrid Cluster Transport](./HYBRID_CLUSTER.md)** - Redis + RabbitMQ hybrid transport solution
- **[Cluster Validation (real middleware)](./CLUSTER_VALIDATION.md)** - Every transport verified against a real Redis cluster + RabbitMQ, plus production compatibility notes
- **[Hybrid Deployment Checklist](./HYBRID_DEPLOYMENT_CHECKLIST.md)** - Item-by-item pre-production checklist for the Hybrid transport
- **[Metrics](./METRICS.md)** - OpenTelemetry integration, performance monitoring
- **[Dashboard](./DASHBOARD.md)** - Monitoring panel, API interfaces, frontend UI

## 🚀 Quick Navigation

### I'm a beginner, where do I start?

1. Read [Quick Start](./QUICK_START.md) to understand basic concepts
2. Check [Core Library](./CORE.md) to learn basic features
3. Refer to sample projects for practical applications

### I need to implement cluster functionality

1. Read [Cluster Module](./CLUSTER.md) to understand cluster architecture
2. Choose transport: WebSocket, Redis, RabbitMQ, or Hybrid
3. Check [Cluster Transports](./CLUSTER_TRANSPORTS.md) for extension packages
4. For automatic discovery and load balancing, see [Hybrid Cluster Transport](./HYBRID_CLUSTER.md)

### I need monitoring and statistics

1. Read [Metrics](./METRICS.md) to understand monitoring features
2. Check [Dashboard](./DASHBOARD.md) for visualization interface
3. Configure OpenTelemetry to export metrics

### I need to understand configuration options

1. Read [Configuration Guide](./CONFIGURATION.md) for all configurations
2. Refer to [API Reference](./API_REFERENCE.md) for detailed parameters
3. Check configuration files in sample projects

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

## 🔧 Supported .NET Versions

- .NET Standard 2.1
- .NET 6.0
- .NET 7.0
- .NET 8.0
- .NET 9.0

## 📝 Version Information

Current Version: **1.7.8**

Check [CHANGELOG.md](../../CHANGELOG.md) for version history.

## 🤝 Contributing

Contributions are welcome! Please check [CONTRIBUTING.md](../../CONTRIBUTING.md) for contribution guidelines.

## 📄 License

This project is licensed under [LICENSE](../../LICENSE).

## 🔗 Related Links

- [GitHub Repository](https://github.com/Cyaim/WebSocketServer)
- [NuGet Package](https://www.nuget.org/packages/Cyaim.WebSocketServer)
- [Issue Tracker](https://github.com/Cyaim/WebSocketServer/issues)

## 💡 Get Help

- Check the troubleshooting section in the documentation
- Submit an [Issue](https://github.com/Cyaim/WebSocketServer/issues) on GitHub
- Check sample projects for practical usage

---

**Last Updated**: 2024-12-XX

