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
```

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

### 3. Start Cluster Transport

```csharp
var app = builder.Build();

// Get cluster transport and start it
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

app.Run();
```

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
    public NodeStatus Status { get; set; }       // Node status
}
```

### Updating Node Information

You can update node information (e.g., connection count) regularly to enable better load balancing:

```csharp
var discoveryService = new RedisNodeDiscoveryService(
    logger,
    redisService,
    nodeId,
    nodeInfo);

await discoveryService.UpdateNodeInfoAsync(new NodeInfo
{
    NodeId = nodeId,
    Address = "localhost",
    Port = 5001,
    ConnectionCount = currentConnectionCount,
    MaxConnections = 10000,
    CpuUsage = GetCpuUsage(),
    MemoryUsage = GetMemoryUsage(),
    Status = NodeStatus.Active
});
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

## Related Documentation

- [Cluster Module Documentation](./CLUSTER.md)
- [Cluster Transports Documentation](./CLUSTER_TRANSPORTS.md)
- [Configuration Guide](./CONFIGURATION.md)

