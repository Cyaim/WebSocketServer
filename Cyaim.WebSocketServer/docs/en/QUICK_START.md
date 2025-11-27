# Quick Start

Get started with Cyaim.WebSocketServer in 5 minutes.

## Installation

```bash
dotnet add package Cyaim.WebSocketServer
```

## Minimal Example

### 1. Create Project

```bash
dotnet new web -n WebSocketServerApp
cd WebSocketServerApp
```

### 2. Install Package

```bash
dotnet add package Cyaim.WebSocketServer
```

### 3. Configure Program.cs

```csharp
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Configures;

var builder = WebApplication.CreateBuilder(args);

// Configure WebSocket route
builder.Services.ConfigureWebSocketRoute(x =>
{
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", new MvcChannelHandler().ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});

builder.Services.AddControllers();

var app = builder.Build();

app.UseWebSockets();
app.UseWebSocketServer();
app.MapControllers();

app.Run();
```

### 4. Create Controller

```csharp
using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("[controller]")]
public class EchoController : ControllerBase
{
    [WebSocket]
    [HttpGet]
    public string Echo(string message)
    {
        return $"Echo: {message}";
    }
}
```

### 5. Run

```bash
dotnet run
```

### 6. Test

Connect to `ws://localhost:5000/ws` using a WebSocket client and send:

```json
{
    "target": "Echo.Echo",
    "body": {
        "message": "Hello, WebSocket!"
    }
}
```

## Next Steps

- Read [Core Library Documentation](./CORE.md) for detailed features
- Check [Configuration Guide](./CONFIGURATION.md) for configuration options
- Refer to sample projects for practical applications

