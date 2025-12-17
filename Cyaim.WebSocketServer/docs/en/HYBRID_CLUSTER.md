# Hybrid Cluster Transport Documentation

This document provides detailed information about the Hybrid cluster transport implementation in Cyaim.WebSocketServer, which uses **Redis for service discovery** and **RabbitMQ for message routing**.

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Features](#features)
- [Quick Start](#quick-start)
- [Installation](#installation)
- [Configuration](#configuration)
- [Load Balancing Strategies](#load-balancing-strategies)
- [Node Information Management](#node-information-management)
- [Custom Implementations](#custom-implementations)
- [Best Practices](#best-practices)
- [Troubleshooting](#troubleshooting)

## Overview

Hybrid cluster transport is a cluster transport solution that combines the advantages of Redis and RabbitMQ:

- **Redis** - Used for service discovery and node information storage, supports automatic node registration and discovery
- **RabbitMQ** - Used for message routing, provides reliable message delivery guarantees
- **Load Balancing** - Supports multiple load balancing strategies to intelligently select optimal nodes

### Why Choose Hybrid Solution?

Compared to single transport methods, the Hybrid solution has the following advantages:

1. **Decoupled Service Discovery and Message Routing** - Service discovery uses Redis's lightweight features, message routing uses RabbitMQ's reliability guarantees
2. **Automatic Node Discovery** - No need to manually configure node lists, new nodes automatically join the cluster
3. **Intelligent Load Balancing** - Select optimal nodes based on connection count, CPU, memory, and other metrics
4. **High Availability** - Both Redis and RabbitMQ support cluster mode, providing high availability guarantees

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│              HybridClusterTransport                     │
│  (Implements IClusterTransport)                        │
└──────────────┬──────────────────────────────────────────┘
               │
    ┌──────────┴──────────┐
    │                     │
┌───▼──────┐      ┌────────▼────────┐
│   Redis  │      │    RabbitMQ     │
│          │      │                 │
│ Service  │      │  Message Queue  │
│Discovery │      │    Service      │
│          │      │                 │
│ Node     │      │  Exchange &     │
│ Registry │      │  Queue Routing  │
└──────────┘      └─────────────────┘
```

### Component Responsibilities

- **Redis**: Stores node information, enables automatic discovery and registration
- **RabbitMQ**: Routes WebSocket messages between nodes
- **LoadBalancer**: Selects optimal node based on strategy

## Features

- ✅ **Redis-based Service Discovery** - Automatic node registration and discovery
- ✅ **RabbitMQ Message Routing** - Efficient message routing between nodes
- ✅ **Connection Route Storage** - Store connection routes in Redis for fast queries without broadcasting
- ✅ **Automatic Route Refresh** - Automatically refresh connection route expiration to prevent loss
- ✅ **Automatic Load Balancing** - Multiple load balancing strategies
- ✅ **Abstraction Layer** - Support different Redis and RabbitMQ libraries
- ✅ **Node Health Monitoring** - Automatic detection of offline nodes
- ✅ **Connection Count Tracking** - Real-time connection statistics

## Quick Start

### 1. Install Packages

**Example: StackExchange.Redis + RabbitMQ**

```bash
# Core package
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid

# Redis implementation (choose one)
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange

# Message queue implementation
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ
```

**Example: FreeRedis + RabbitMQ**

```bash
# Core package
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid

# Redis implementation
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis

# Message queue implementation
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ
```

### 2. Configure Services

**Example: StackExchange.Redis + RabbitMQ**

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Register StackExchange.Redis service
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<StackExchangeRedisService>>();
    return new StackExchangeRedisService(logger, "localhost:6379");
});

// Register RabbitMQ service
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});

// Register hybrid cluster transport
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        Endpoint = "/ws",
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});
```

**Example: FreeRedis + RabbitMQ**

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Register FreeRedis service
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<FreeRedisService>>();
    return new FreeRedisService(logger, "localhost:6379");
});

// Register RabbitMQ service
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});

// Register hybrid cluster transport
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        Endpoint = "/ws",
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});
```

### 3. Configure WebSocket Server (Required)

⚠️ **Important**: When using cluster transport, you **MUST** still configure the WebSocket server with `ConfigureWebSocketRoute`. The cluster transport (`IClusterTransport`) only handles inter-node communication, not client connections. Client WebSocket connections still need to go through the regular WebSocket server channels.

```csharp
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

// Configure WebSocket server channels
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    
    // Define business channels for client connections
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }  // ← Required: Client connection endpoint
    };
    x.ApplicationServiceCollection = builder.Services;
});
```

### 4. Start Cluster Transport

```csharp
var app = builder.Build();

// Configure WebSocket middleware
app.UseWebSockets();
app.UseWebSocketServer();

// Get cluster transport and start it
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

app.Run();
```

## ⚠️ Important Notes

### Single Node Configuration Must Be Retained

**Key Point**: `IClusterTransport` (including `HybridClusterTransport`) is **only** responsible for inter-node communication (service discovery and message routing). It does **NOT** handle client WebSocket connections.

#### Architecture Overview

```text
┌─────────────────────────────────────────────────────────┐
│         WebSocket Server (Single Node Config)          │
│  ┌──────────────────────────────────────────────────┐  │
│  │  ConfigureWebSocketRoute                          │  │
│  │  └── /ws (Business Channel) ← Clients connect    │  │
│  └──────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
                        │
                        │ Uses IClusterTransport
                        ▼
┌─────────────────────────────────────────────────────────┐
│      HybridClusterTransport (Cluster Transport)         │
│  ┌──────────────┐         ┌──────────────┐            │
│  │ Redis        │         │ RabbitMQ    │            │
│  │ (Discovery)  │         │ (Routing)   │            │
│  └──────────────┘         └──────────────┘            │
└─────────────────────────────────────────────────────────┘
```

#### Responsibilities

| Component | Responsibility | Required |
|-----------|---------------|----------|
| `ConfigureWebSocketRoute` | Handle client WebSocket connections | ✅ **Yes** |
| `IClusterTransport` | Inter-node communication | ✅ **Yes** |

#### Complete Configuration Example

Here's a complete example showing both configurations:

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// ============================================
// Step 1: Configure Redis Service
// ============================================
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<FreeRedisService>>();
    return new FreeRedisService(logger, "localhost:6379");
});

// ============================================
// Step 2: Configure RabbitMQ Service
// ============================================
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});

// ============================================
// Step 3: Configure WebSocket Server
// ⚠️ THIS IS REQUIRED - DO NOT SKIP
// ============================================
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    
    // Define business channels for client connections
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }  // Client connection endpoint
    };
    x.ApplicationServiceCollection = builder.Services;
});

// ============================================
// Step 4: Configure Cluster Transport
// ============================================
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        Endpoint = "/ws",  // This is just for node info, NOT a replacement for ConfigureWebSocketRoute
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});

var app = builder.Build();

// ============================================
// Step 5: Configure Middleware
// ============================================
app.UseWebSockets();
app.UseWebSocketServer();  // Required for WebSocket server

// ============================================
// Step 6: Start Cluster Transport
// ============================================
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

app.Run();
```

#### Common Mistakes

❌ **Wrong**: Only configuring `IClusterTransport` without `ConfigureWebSocketRoute`

```csharp
// ❌ This will NOT work - clients cannot connect
builder.Services.AddSingleton<IClusterTransport>(...);
// Missing ConfigureWebSocketRoute
```

✅ **Correct**: Configuring both `ConfigureWebSocketRoute` and `IClusterTransport`

```csharp
// ✅ Correct - both are required
builder.Services.ConfigureWebSocketRoute(...);  // For client connections
builder.Services.AddSingleton<IClusterTransport>(...);  // For inter-node communication
```

#### Why Both Are Needed

1. **Client Connections**:
   - Clients connect to `/ws` endpoint configured via `ConfigureWebSocketRoute`
   - This is handled by the WebSocket server, not the cluster transport

2. **Inter-Node Communication**:
   - When a message needs to be sent to a client on another node, `IClusterTransport` routes it
   - This uses Redis for discovery and RabbitMQ for routing

#### Summary

- ✅ **Always configure** `ConfigureWebSocketRoute` for client connections
- ✅ **Always configure** `IClusterTransport` for inter-node communication
- ❌ **Never skip** `ConfigureWebSocketRoute` when using cluster transport

## Installation

### Core Package

```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid
```

### Implementation Packages (Modular Design)

The Hybrid cluster transport uses a modular design. You can choose the implementations you need:

#### Redis Implementations (Service Discovery)

Choose one Redis implementation for service discovery:

**Option 1: StackExchange.Redis**
```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange
```

**Option 2: FreeRedis**
```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis
```

#### Message Queue Implementations (Message Routing)

Choose one message queue implementation for message routing:

**RabbitMQ**
```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ
```

**Future implementations**:
- `Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.MQTT` - MQTT support

### ⚠️ Deprecated Package

The old `Cyaim.WebSocketServer.Cluster.Hybrid.Implementations` package is deprecated. Please use the new modular packages instead.

## Configuration

### Redis Connection String

Supports the following formats:

```
localhost:6379
redis://localhost:6379
redis://password@localhost:6379
```

### RabbitMQ Connection String

Supports standard AMQP format:

```
amqp://guest:guest@localhost:5672/
amqp://username:password@host:port/vhost
```

### Cluster Prefix

The default Redis key prefix is `websocket:cluster`. You can customize it through the `RedisNodeDiscoveryService` constructor:

```csharp
var discoveryService = new RedisNodeDiscoveryService(
    logger,
    redisService,
    nodeId,
    nodeInfo,
    clusterPrefix: "myapp:cluster"  // Custom prefix
);
```

## Load Balancing Strategies

Hybrid cluster transport supports the following load balancing strategies:

### LeastConnections (Default)

Selects the node with the fewest connections.

```csharp
LoadBalancingStrategy.LeastConnections
```

### RoundRobin

Selects nodes in rotation order.

```csharp
LoadBalancingStrategy.RoundRobin
```

### LeastResourceUsage

Selects the node with the lowest CPU and memory usage.

```csharp
LoadBalancingStrategy.LeastResourceUsage
```

### Random

Randomly selects a node.

```csharp
LoadBalancingStrategy.Random
```

## Node Information Management

### NodeInfo Class

The `NodeInfo` class contains information about each cluster node:

```csharp
public class NodeInfo
{
    public string NodeId { get; set; }           // Node ID
    public string Address { get; set; }          // Node address
    public int Port { get; set; }                // Node port
    public string Endpoint { get; set; }         // WebSocket endpoint
    public int ConnectionCount { get; set; }     // Current connections
    public int MaxConnections { get; set; }      // Max connections
    public double CpuUsage { get; set; }         // CPU usage %
    public double MemoryUsage { get; set; }      // Memory usage %
    public DateTime LastHeartbeat { get; set; }  // Last heartbeat
    public DateTime RegisteredAt { get; set; }   // Registration time
    public NodeStatus Status { get; set; }       // Node status
    public Dictionary<string, string> Metadata { get; set; }  // Metadata
}
```

### ✅ Auto-Update Node Information (Enabled by Default)

**Good News**: `HybridClusterTransport` **automatically updates** node information by default! During each heartbeat (every 5 seconds), the system automatically:

1. **Auto-detects connection count** - Gets from `MvcChannelHandler.Clients` or `ClusterManager`
2. **Auto-gets CPU usage** - Calculated from process information
3. **Auto-gets memory usage** - Retrieved from process information

**No additional configuration needed** - node information stays up-to-date automatically!

If you need custom update logic, you can pass a `nodeInfoProvider` parameter.

### Auto-Update Mechanism

#### Default Auto-Update (No Configuration Needed)

`HybridClusterTransport` **automatically updates** node information by default, no additional configuration needed:

```csharp
// Standard configuration - auto-update is enabled
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        Endpoint = "/ws",
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    // No additional configuration needed - auto-update is enabled!
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});
```

**Auto-update will**:
- ✅ Automatically update every 5 seconds (heartbeat interval)
- ✅ Automatically get connection count from `MvcChannelHandler.Clients`
- ✅ Automatically get connection count from `ClusterManager` (if available)
- ✅ Automatically calculate CPU usage
- ✅ Automatically get memory usage

#### Custom Update Logic

If you need custom update logic, you can pass a `nodeInfoProvider` parameter:

```csharp
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        Endpoint = "/ws",
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    // Custom node info provider
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections,
        nodeInfoProvider: async () =>
        {
            // Custom logic: get information from your business system
            return new NodeInfo
            {
                NodeId = "node1",
                Address = "localhost",
                Port = 5001,
                Endpoint = "/ws",
                ConnectionCount = YourCustomService.GetConnectionCount(),
                MaxConnections = 10000,
                CpuUsage = YourCustomService.GetCpuUsage(),
                MemoryUsage = YourCustomService.GetMemoryUsage(),
                Status = NodeStatus.Active
            };
        }
    );
});
```

#### Manual Update (Optional)

If you need to update node information immediately (without waiting for heartbeat), you can call `UpdateNodeInfoAsync`:

```csharp
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.UpdateNodeInfoAsync(new NodeInfo
{
    NodeId = "node1",
    Address = "localhost",
    Port = 5001,
    Endpoint = "/ws",
    ConnectionCount = 100,
    MaxConnections = 10000,
    CpuUsage = 25.5,
    MemoryUsage = 512.0,
    Status = NodeStatus.Active
});
```

### Getting Current Connection Count

You need to implement your own method to get the current WebSocket connection count. Here are some examples:

```csharp
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Cluster;

private int GetCurrentConnectionCount()
{
    // Method 1: Get from MvcChannelHandler's static Clients collection (if using MVC handler)
    // This is the simplest method, suitable for scenarios using MvcChannelHandler
    return MvcChannelHandler.Clients?.Count ?? 0;
    
    // Method 2: Get from ClusterManager (if cluster is enabled)
    // var clusterManager = GlobalClusterCenter.ClusterManager;
    // if (clusterManager != null)
    // {
    //     return clusterManager.GetLocalConnectionCount();
    // }
    // return 0;
    
    // Method 3: Use static counter (update on connect/disconnect)
    // return WebSocketConnectionCounter.CurrentCount;
    
    // Method 4: Get from your business logic
    // return YourService.GetActiveConnectionCount();
}

private double GetCpuUsage()
{
    // Note: Getting CPU usage requires cross-platform compatibility considerations
    // Here's a simple example; you may need to use PerformanceCounter or other libraries
    
    // Windows platform can use PerformanceCounter
    // #if WINDOWS
    // using System.Diagnostics;
    // var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
    // cpuCounter.NextValue(); // First call returns 0
    // await Task.Delay(100);
    // return cpuCounter.NextValue();
    // #endif
    
    // Cross-platform solution: Use System.Diagnostics.Process to get process CPU time
    // But this is cumulative CPU time, not real-time usage
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var totalProcessorTime = process.TotalProcessorTime.TotalMilliseconds;
    var uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
    return uptime > 0 ? (totalProcessorTime / uptime) * 100 : 0.0;
}

private double GetMemoryUsage()
{
    // Get current process memory usage (MB)
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var workingSet = process.WorkingSet64;
    return (double)workingSet / (1024 * 1024); // Convert to MB
    
    // Or get memory usage percentage (need to know total memory)
    // var totalMemory = // Need to get total memory from system
    // return (double)workingSet / totalMemory * 100;
}
```

### Complete Example: Periodically Update Node Information

```csharp
var app = builder.Build();

// Configure WebSocket middleware
app.UseWebSockets();
app.UseWebSocketServer();

// Start cluster transport
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

// Periodically update node info (every 5 seconds)
var updateTimer = new System.Timers.Timer(5000);
updateTimer.Elapsed += async (sender, e) =>
{
    try
    {
        // Get current connection count
        var connectionCount = MvcChannelHandler.Clients?.Count ?? 0;
        
        // Get system resource usage
        var cpuUsage = GetCpuUsage();
        var memoryUsage = GetMemoryUsage();
        
        // Update node information
        await clusterTransport.UpdateNodeInfoAsync(new NodeInfo
        {
            NodeId = "node1",
            Address = "localhost",
            Port = 5001,
            Endpoint = "/ws",
            ConnectionCount = connectionCount,  // ← This will update to Redis
            MaxConnections = 10000,
            CpuUsage = cpuUsage,                // ← This will update to Redis
            MemoryUsage = memoryUsage,          // ← This will update to Redis
            Status = NodeStatus.Active
        });
        
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogDebug($"Updated node info: Connections={connectionCount}, CPU={cpuUsage:F2}%, Memory={memoryUsage:F2}MB");
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to update node info");
    }
};
updateTimer.Start();

app.Run();
```

## Custom Implementations

You can implement `IRedisService` and `IMessageQueueService` interfaces to use different libraries.

### Custom Redis Implementation

```csharp
public class CustomRedisService : IRedisService
{
    // Implement IRedisService interface
    public Task ConnectAsync() { /* ... */ }
    public Task DisconnectAsync() { /* ... */ }
    public Task<string> GetAsync(string key) { /* ... */ }
    public Task SetAsync(string key, string value, TimeSpan? expiry = null) { /* ... */ }
    public Task<bool> ExistsAsync(string key) { /* ... */ }
    public Task DeleteAsync(string key) { /* ... */ }
    public Task<List<string>> GetKeysAsync(string pattern) { /* ... */ }
    public Task SubscribeAsync(string channel, Action<string, string> handler) { /* ... */ }
    public Task UnsubscribeAsync(string channel) { /* ... */ }
    public Task PublishAsync(string channel, string message) { /* ... */ }
}
```

### Custom Message Queue Implementation

```csharp
public class CustomMessageQueueService : IMessageQueueService
{
    // Implement IMessageQueueService interface
    public Task ConnectAsync() { /* ... */ }
    public Task DisconnectAsync() { /* ... */ }
    public Task<string> DeclareExchangeAsync(string exchange, string type, bool durable = false) { /* ... */ }
    public Task<string> DeclareQueueAsync(string queue, bool durable = false, bool exclusive = false, bool autoDelete = false) { /* ... */ }
    public Task BindQueueAsync(string queue, string exchange, string routingKey) { /* ... */ }
    public Task PublishAsync(string exchange, string routingKey, byte[] body, MessageProperties properties = null) { /* ... */ }
    public Task ConsumeAsync(string queue, Func<byte[], MessageProperties, Task> handler, bool autoAck = true) { /* ... */ }
}
```

## Best Practices

1. **Update Node Info Regularly** - Update connection count and resource usage for accurate load balancing
2. **Monitor Node Health** - Check `LastHeartbeat` to detect offline nodes
3. **Set Appropriate MaxConnections** - Helps load balancer make better decisions
4. **Use LeastConnections for High Traffic** - Most effective for WebSocket connections
5. **Handle Node Failures Gracefully** - Implement retry logic for message sending

## Complete Example

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Configure FreeRedis (or use StackExchangeRedisService from Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange)
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<FreeRedisService>>();
    return new FreeRedisService(logger, "localhost:6379");
});

// Configure RabbitMQ
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});

// Configure Hybrid Cluster Transport
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = Environment.GetEnvironmentVariable("NODE_ID") ?? "node1",
        Address = "localhost",
        Port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "5001"),
        Endpoint = "/ws",
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: nodeInfo.NodeId,
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});

var app = builder.Build();

// Start cluster transport
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

// Update node info periodically
var timer = new System.Timers.Timer(5000); // Every 5 seconds
timer.Elapsed += async (sender, e) =>
{
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        ConnectionCount = GetCurrentConnectionCount(),
        MaxConnections = 10000,
        CpuUsage = GetCpuUsage(),
        MemoryUsage = GetMemoryUsage(),
        Status = NodeStatus.Active
    };
    
    // Update via discovery service
    // Note: You'll need to access the discovery service instance
};

timer.Start();

app.Run();
```

## Connection Route Storage

### Redis-Based Connection Route Storage

`HybridClusterTransport` supports storing connection route information in Redis for fast queries without broadcasting:

#### How It Works

1. **On Connection Registration**:
   - Store connection route info to Redis: `websocket:cluster:connections:{connectionId}` -> `{nodeId}`
   - Store connection metadata: `websocket:cluster:connection:metadata:{connectionId}` -> JSON
   - Set 24-hour expiration

2. **On Connection Query**:
   - Query directly from Redis, no broadcast needed
   - If found, update local routing table cache

3. **On Connection Unregistration**:
   - Remove connection route info and metadata from Redis

4. **Automatic Refresh**:
   - Every 12 hours, automatically refresh route info for all local active connections
   - Reset 24-hour expiration to ensure active connections' route info doesn't expire

#### Advantages

- ✅ **Fast Query** - Direct read from Redis, low latency
- ✅ **No Broadcasting** - Reduces RabbitMQ message volume
- ✅ **Auto Sync** - All nodes share the same Redis, routing table auto-syncs
- ✅ **Auto Refresh** - Active connections' route info won't be lost due to expiration

#### Implementation Details

Connection route storage is implemented through optional methods in the `IClusterTransport` interface:

- `StoreConnectionRouteAsync` - Store connection route information
- `GetConnectionRouteAsync` - Get connection route information
- `RemoveConnectionRouteAsync` - Remove connection route information
- `RefreshConnectionRouteAsync` - Refresh connection route expiration

Only `HybridClusterTransport` implements these methods. Other transports (WebSocket, Redis Pub/Sub, RabbitMQ) don't support this feature and will automatically fall back to broadcasting.

#### Configuration

Connection route storage is **automatically enabled**, no configuration needed. When using `HybridClusterTransport`:

- Connection routes are automatically stored to Redis on registration
- Connection routes are automatically queried from Redis on query
- Local connections' routes are automatically refreshed every 12 hours

#### Redis Key Format

```
websocket:cluster:connections:{connectionId} -> {nodeId}
websocket:cluster:connection:metadata:{connectionId} -> JSON
```

#### Automatic Refresh Mechanism

The system periodically (every 12 hours) checks all local active connections and refreshes their expiration in Redis:

- Only refreshes local connections (`nodeId == current node ID`)
- Verifies connections still exist in local routing table
- If refresh fails, attempts to re-store route info
- Logs count of successful and failed refreshes

This ensures that even if connections exist for more than 24 hours, route info won't be lost due to expiration.

## Message Deduplication Mechanism

### Automatic Duplicate Prevention

`HybridClusterTransport` has built-in message deduplication to ensure messages are not processed multiple times:

1. **Target Node Check** - Only the target node processes messages intended for it
2. **Message ID Deduplication** - ~~Uses message ID to prevent processing the same message twice~~ (Removed per user request)
3. **Automatic Cleanup** - ~~Periodically cleans up expired message ID records (every 5 minutes, keeps records from last 10 minutes)~~ (Removed per user request)

> **Note**: Message deduplication mechanism has been removed per user request. The system no longer filters duplicate message IDs.

### How It Works

```text
Message Arrives → Check if from self → Check target node → Process message
```

#### Target Node Check

- If message's `ToNodeId` is not empty and not equal to current node ID, message is ignored
- If `ToNodeId` is `null` (broadcast message), all nodes process it
- If `ToNodeId` equals current node ID, message is processed

> **Note**: Message ID deduplication has been removed. The system no longer checks if a message ID has been processed.

### Example Scenarios

**Scenario 1: Unicast Message (Specific Target Node)**

```csharp
// Node 1 sends message to Node 2
await clusterTransport.SendAsync("node2", new ClusterMessage { ... });

// Result:
// - Node 2: Processes message ✅
// - Node 1, Node 3: Ignore message (because ToNodeId != own node ID) ✅
```

**Scenario 2: Broadcast Message**

```csharp
// Node 1 broadcasts message
await clusterTransport.BroadcastAsync(new ClusterMessage { ... });

// Result:
// - All nodes: Receive and process message ✅
```

**Scenario 3: Duplicate Message Arrival (Network Retransmission, etc.)**

```csharp
// If same message arrives multiple times due to network issues
// Result:
// - All duplicates: Processed normally ✅
// Note: If business logic requires deduplication, implement it at the application layer
```

### Configuration

Target node check is **automatically enabled**, no configuration needed. The system will:

- Automatically check target node
- Automatically ignore messages sent to other nodes

> **Note**: Message ID deduplication has been removed. The system no longer caches or checks message IDs.

## Troubleshooting

### Nodes Not Discovering Each Other

- Check Redis connection
- Verify cluster prefix matches
- Check Redis keys: `websocket:cluster:nodes:*`

### Messages Not Routing

- Check RabbitMQ connection
- Verify exchange and queue declarations
- Check RabbitMQ management UI

### Load Balancing Not Working

- Ensure node info is updated regularly
- Check connection count values
- Verify node status is `Active`

### Messages Being Processed Multiple Times

- ⚠️ **Note**: Message deduplication mechanism has been removed. The system no longer filters duplicate message IDs.
- If business logic requires deduplication, implement it at the application layer:
  - Check message ID in message processing logic
  - Use distributed locks or cache to record processed message IDs
  - Implement deduplication logic based on business requirements
- Check whether message's `ToNodeId` is correctly set
- Check logs for "Ignoring message - target node is" messages (this is normal, indicating correct message routing)

## Related Documentation

- [Cluster Module Documentation](./CLUSTER.md)
- [Cluster Transports Documentation](./CLUSTER_TRANSPORTS.md)
- [Configuration Guide](./CONFIGURATION.md)

