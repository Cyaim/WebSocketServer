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
    
Example Code:
```C#

// mark WebSocket 
[WebSocket()]
[HttpGet]
public IEnumerable<WeatherForecast> Get()
{
    var rng = new Random();
    return Enumerable.Range(1, 5).Select(index => new WeatherForecast
    {
         Date = DateTime.Now.AddDays(index),
         TemperatureC = rng.Next(-20, 55),
         Summary = Summaries[rng.Next(Summaries.Length)]
    }).ToArray();
}
```

## Request and Response

> Scheme namespace
> Request Cyaim.WebSocketServer.Infrastructure.Handlers.MvcRequestScheme
> Response Cyaim.WebSocketServer.Infrastructure.Handlers.MvcResponseScheme

> Request scheme
1. Nonparametric method request
```json
{
	"target": "WeatherForecast.Get",
	"body": {}
}
```
This request will be located at "WeatherForecastController" -> "Get" Method.  

> Response to this request
```json
{
	"Status": 0,
	"Msg": null,
	"RequestTime": 637395762382112345,
	"ComplateTime": 637395762382134526,
	"Body": [{
		"Date": "2020-10-30T13:50:38.2133285+08:00",
		"TemperatureC": 43,
		"TemperatureF": 109,
		"Summary": "Scorching"
	}, {
		"Date": "2020-10-31T13:50:38.213337+08:00",
		"TemperatureC": 1,
		"TemperatureF": 33,
		"Summary": "Chilly"
	}, {
		"Date": "2020-11-01T13:50:38.2133373+08:00",
		"TemperatureC": 0,
		"TemperatureF": 32,
		"Summary": "Cool"
	}, {
		"Date": "2020-11-02T13:50:38.2133374+08:00",
		"TemperatureC": -2,
		"TemperatureF": 29,
		"Summary": "Hot"
	}, {
		"Date": "2020-11-03T13:50:38.2133376+08:00",
		"TemperatureC": 43,
		"TemperatureF": 109,
		"Summary": "Hot"
	}]
}
```
Forward invoke method return content will write MvcResponseScheme.Body.  

2. Request with parameters

