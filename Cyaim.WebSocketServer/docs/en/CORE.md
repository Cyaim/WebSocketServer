# Core Library Documentation

This document provides detailed information about the features and usage of the Cyaim.WebSocketServer core library.

## Table of Contents

- [Overview](#overview)
- [Quick Start](#quick-start)
- [Routing System](#routing-system)
- [Handlers](#handlers)
- [Middleware](#middleware)
- [Configuration Options](#configuration-options)
- [Request and Response](#request-and-response)
- [Advanced Features](#advanced-features)

## Overview

Cyaim.WebSocketServer is a lightweight, high-performance WebSocket server library that provides MVC-like routing mechanisms, supporting full-duplex communication, multiplexing, and pipeline processing.

### Core Features

- ✅ **Lightweight & High Performance** - Based on ASP.NET Core, excellent performance
- ✅ **Routing System** - MVC-like routing mechanism, supports RESTful API
- ✅ **Full Duplex Communication** - Supports bidirectional communication
- ✅ **Multiplexing** - Single connection supports multiple requests/responses
- ✅ **Pipeline Processing** - Supports middleware pipeline pattern
- ✅ **Type Safety** - Strongly typed parameter binding and responses

## Quick Start

### 1. Install NuGet Package

```bash
dotnet add package Cyaim.WebSocketServer
```

### 2. Configure Services

```csharp
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Configures;

var builder = WebApplication.CreateBuilder(args);

// Configure WebSocket route
builder.Services.ConfigureWebSocketRoute(x =>
{
    // Define channels
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", new MvcChannelHandler(4 * 1024).ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});
```

### 3. Configure Middleware

```csharp
var app = builder.Build();

// Configure WebSocket options
var webSocketOptions = new WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();
```

### 4. Create Controller

```csharp
using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    [WebSocket]
    [HttpGet]
    public IEnumerable<WeatherForecast> Get()
    {
        return new[]
        {
            new WeatherForecast { Date = DateTime.Now, TemperatureC = 25 }
        };
    }
}
```

## Routing System

### Channel Configuration

Channels are entry points for WebSocket connections, with each channel corresponding to a handler.

```csharp
x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
{
    { "/ws", mvcHandler.ConnectionEntry },      // MVC handler
    { "/api", customHandler.ConnectionEntry }    // Custom handler
};
```

### Endpoint Marking

Use the `[WebSocket]` attribute to mark WebSocket endpoints:

```csharp
[WebSocket]
[HttpGet]
public string GetMessage()
{
    return "Hello, WebSocket!";
}
```

**Note**: The `method` parameter of the `[WebSocket]` attribute is case-insensitive.

## Handlers

### MvcChannelHandler

`MvcChannelHandler` is the default MVC-style handler that supports routing to controllers and action methods.

#### Constructor Parameters

```csharp
// Receive and send buffer sizes (default 4KB)
var handler = new MvcChannelHandler(
    receiveBufferSize: 4 * 1024,
    sendBufferSize: 4 * 1024
);
```

#### Configuration Options

```csharp
var handler = new MvcChannelHandler();

// Receive buffer sizes
handler.ReceiveTextBufferSize = 8 * 1024;
handler.ReceiveBinaryBufferSize = 8 * 1024;

// Send buffer sizes
handler.SendTextBufferSize = 8 * 1024;
handler.SendBinaryBufferSize = 8 * 1024;

// Response send timeout
handler.ResponseSendTimeout = TimeSpan.FromSeconds(30);
```

### Custom Handlers

Implement the `IWebSocketHandler` interface to create custom handlers:

```csharp
public class CustomHandler : IWebSocketHandler
{
    public WebSocketHandlerMetadata Metadata { get; } = new WebSocketHandlerMetadata
    {
        Describe = "Custom handler",
        CanHandleBinary = true,
        CanHandleText = true
    };

    public async Task ConnectionEntry(HttpContext context, WebSocket webSocket)
    {
        // Handle connection logic
    }
}
```

## Middleware

### WebSocketRouteMiddleware

`WebSocketRouteMiddleware` is the core middleware responsible for:

- Routing WebSocket requests to corresponding channels
- Managing WebSocket connection lifecycle
- Handling connection upgrades

### Usage

```csharp
app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();
```

## Configuration Options

### WebSocketRouteOption

```csharp
builder.Services.ConfigureWebSocketRoute(x =>
{
    // Channel configuration
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>();
    
    // Service collection (for dependency injection)
    x.ApplicationServiceCollection = builder.Services;
    
    // Bandwidth limit policy (optional)
    x.BandwidthLimitPolicy = new BandwidthLimitPolicy
    {
        Enabled = true,
        // ... configuration
    };
    
    // Cluster configuration (optional)
    x.EnableCluster = false;
});
```

### WebSocketOptions

```csharp
var webSocketOptions = new WebSocketOptions()
{
    // Keep-Alive interval
    KeepAliveInterval = TimeSpan.FromSeconds(120),
    
    // Receive buffer size (deprecated, configure in handler)
    // ReceiveBufferSize = 4 * 1024
};
```

## Request and Response

### Request Format

Requests use JSON format, following the `MvcRequestScheme` structure:

```json
{
    "target": "ControllerName.ActionName",
    "body": {
        "param1": "value1",
        "param2": 123
    }
}
```

**Note**: The `target` parameter is case-insensitive.

#### Parameterless Request

```json
{
    "target": "WeatherForecast.Get",
    "body": {}
}
```

#### Request with Parameters

```json
{
    "target": "WeatherForecast.Get",
    "body": {
        "city": "Beijing",
        "date": "2024-01-01"
    }
}
```

### Response Format

Responses use JSON format, following the `MvcResponseScheme` structure:

```json
{
    "Target": "WeatherForecast.Get",
    "Status": 0,
    "Msg": null,
    "RequestTime": 637395762382112345,
    "CompleteTime": 637395762382134526,
    "Body": {
        // Method return value
    }
}
```

#### Response Field Description

- `Target`: Target method of the request
- `Status`: Status code (0 indicates success)
- `Msg`: Error message (if any)
- `RequestTime`: Request time (Ticks)
- `CompleteTime`: Completion time (Ticks)
- `Body`: Method return value

### Message Types

Two message types are supported:

- **Text**: Text messages (JSON format)
- **Binary**: Binary messages

## Advanced Features

### Request Pipeline

`MvcChannelHandler` supports request pipelines, allowing custom logic to be inserted at different stages of request processing:

```csharp
var handler = new MvcChannelHandler();

// Execute before request parsing
handler.RequestPipeline.TryAdd(
    RequestPipelineStage.BeforeParse,
    new ConcurrentQueue<PipelineItem>()
);

// Add pipeline item
handler.RequestPipeline[RequestPipelineStage.BeforeParse].Enqueue(
    new PipelineItem
    {
        Name = "CustomMiddleware",
        Handler = async (context, next) =>
        {
            // Custom logic
            await next();
        }
    }
);
```

### Pipeline Stages

- `BeforeParse`: Before parsing request
- `AfterParse`: After parsing request
- `BeforeInvoke`: Before invoking method
- `AfterInvoke`: After invoking method
- `BeforeSend`: Before sending response
- `AfterSend`: After sending response

### Dependency Injection

Controllers support full dependency injection:

```csharp
public class WeatherForecastController : ControllerBase
{
    private readonly ILogger<WeatherForecastController> _logger;
    private readonly IWeatherService _weatherService;

    public WeatherForecastController(
        ILogger<WeatherForecastController> logger,
        IWeatherService weatherService)
    {
        _logger = logger;
        _weatherService = weatherService;
    }

    [WebSocket]
    [HttpGet]
    public async Task<WeatherForecast> Get(string city)
    {
        return await _weatherService.GetWeatherAsync(city);
    }
}
```

### Parameter Binding

Multiple parameter binding methods are supported:

#### Simple Types

```csharp
[WebSocket]
public string GetMessage(string message)
{
    return $"Received: {message}";
}
```

Request:
```json
{
    "target": "Controller.GetMessage",
    "body": {
        "message": "Hello"
    }
}
```

#### Complex Types

```csharp
public class RequestModel
{
    public string Name { get; set; }
    public int Age { get; set; }
}

[WebSocket]
public string Process(RequestModel model)
{
    return $"Name: {model.Name}, Age: {model.Age}";
}
```

Request:
```json
{
    "target": "Controller.Process",
    "body": {
        "name": "John",
        "age": 30
    }
}
```

#### Arrays and Collections

```csharp
[WebSocket]
public string ProcessList(List<string> items)
{
    return string.Join(", ", items);
}
```

Request:
```json
{
    "target": "Controller.ProcessList",
    "body": {
        "items": ["item1", "item2", "item3"]
    }
}
```

### Async Methods

Full support for async methods:

```csharp
[WebSocket]
public async Task<string> GetAsync(string id)
{
    var data = await _repository.GetAsync(id);
    return data;
}
```

### Error Handling

#### Exception Handling

Exceptions thrown by methods are caught and returned as error responses:

```json
{
    "Target": "Controller.Action",
    "Status": 1,
    "Msg": "Exception message",
    "RequestTime": 123456789,
    "CompleteTime": 123456790,
    "Body": null
}
```

#### Custom Error Handling

Custom error handling can be implemented in the pipeline:

```csharp
handler.RequestPipeline[RequestPipelineStage.AfterInvoke].Enqueue(
    new PipelineItem
    {
        Name = "ErrorHandler",
        Handler = async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                // Custom error handling
                context.Response.Status = 1;
                context.Response.Msg = ex.Message;
            }
        }
    }
);
```

## Performance Optimization

### Buffer Sizes

Adjust buffer sizes based on message size:

```csharp
// Small messages (< 4KB)
var handler = new MvcChannelHandler(4 * 1024, 4 * 1024);

// Medium messages (4KB - 64KB)
var handler = new MvcChannelHandler(64 * 1024, 64 * 1024);

// Large messages (> 64KB)
var handler = new MvcChannelHandler(256 * 1024, 256 * 1024);
```

### Connection Management

Use static dictionary to manage connections:

```csharp
// Get all connections
var connections = MvcChannelHandler.Clients;

// Check if connection exists
if (MvcChannelHandler.Clients.TryGetValue(connectionId, out var webSocket))
{
    // Connection exists
}
```

### Concurrent Processing

`MvcChannelHandler` supports concurrent processing of multiple requests, with each connection processed independently.

## Best Practices

1. **Use Dependency Injection** - Fully utilize ASP.NET Core's dependency injection system
2. **Async Methods** - Use async methods for I/O operations to improve performance
3. **Error Handling** - Properly handle exceptions and return meaningful error messages
4. **Parameter Validation** - Validate parameters in methods to ensure data validity
5. **Logging** - Use logging to record important operations and errors

## Example Code

For complete examples, refer to:
- [Sample.Dashboard](../../Sample/Cyaim.WebSocketServer.Sample.Dashboard/)
- [Example.Wpf](../../Sample/Cyaim.WebSocketServer.Example.Wpf/)

## Related Documentation

- [Configuration Guide](./CONFIGURATION.md)
- [API Reference](./API_REFERENCE.md)
- [Cluster Module](./CLUSTER.md)
- [Metrics](./METRICS.md)

