# WebSocketServer
> WebSocketServer is lightweight and high performance WebSocket library.support route, full duplex communication.

# QuickStart

1. Install library
    - Install-Package Cyaim.WebSocketServer -Version 1.0.0
2. Configure middleware
- Configure websocket route
```C#
services.ConfigureWebSocketRoute(x =>
{
                //Define channels
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws",new WebSocketChannelHandler().MvcChannelHandler}
    };

});
```

- Configure middleware
```#
var webSocketOptions = new WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120),
    ReceiveBufferSize = 4 * 1024
};
app.UseWebSockets(webSocketOptions);
app.UseWebSocketRoute(app.ApplicationServices);
```

3. Mark WebSocket Endpoints
    - Go to Controller -> Action
    - Add attribute [WebSocket]
```
