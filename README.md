# WebSocketServer
| 996ICU | Version | NuGet | Build | Code Size | License |
|--|--|--|--|--|--|
[![996.icu](https://img.shields.io/badge/link-996.icu-red.svg)](https://996.icu)|[![](https://img.shields.io/badge/.NET%20Standard-2.1-violet.svg)![](https://img.shields.io/badge/.NET%206+-black%20.svg)](https://www.nuget.org/packages/Cyaim.WebSocketServer)|[![](https://img.shields.io/nuget/v/Cyaim.WebSocketServer.svg)](https://www.nuget.org/packages/Cyaim.WebSocketServer)[![NuGet](https://img.shields.io/nuget/dt/Cyaim.WebSocketServer.svg)](https://www.nuget.org/packages/Cyaim.WebSocketServer)|[![](https://github.com/Cyaim/WebSocketServer/workflows/.NET%20Core/badge.svg)](https://github.com/Cyaim/WebSocketServer)|[![Code size](https://img.shields.io/github/languages/code-size/Cyaim/WebSocketServer?logo=github&logoColor=white)](https://github.com/Cyaim/WebSocketServer)|[![License](https://img.shields.io/github/license/Cyaim/WebSocketServer?logo=open-source-initiative&logoColor=green)](https://github.com/Cyaim/WebSocketServer/blob/master/LICENSE)[![LICENSE](https://img.shields.io/badge/license-Anti%20996-blue.svg)](https://github.com/996icu/996.ICU/blob/master/LICENSE)

> WebSocketServer is lightweight and high performance WebSocket library.Support route, full duplex communication.

# QuickStart

1. Install library
> Install-Package Cyaim.WebSocketServer
2. Configure middleware
- Configure websocket route
```C#
services.ConfigureWebSocketRoute(x =>
{
    //Define channels
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws",new MvcChannelHandler(4*1024).ConnectionEntry}
    };
    x.ApplicationServiceCollection = services;
});
```

- Configure middleware
```#
var webSocketOptions = new WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120),

    // This configuration has been deprecated
    ReceiveBufferSize = 4 * 1024
};
app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();
```

- Minimal API can be use
```C#
builder.Services.ConfigureWebSocketRoute(x =>
{
    //Define channels
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws",new MvcChannelHandler(4*1024).ConnectionEntry}
    };
    x.ApplicationServiceCollection = builder.Services;
});

var webSocketOptions = new WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120),
};
app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();
```

3. Mark WebSocket Endpoints
    - Go to Controller -> Action
    - Add attribute [WebSocket]  
> [WebSocket] -> "method" parameter ignore case
    
Example Code:
```C#
// mark WebSocket 
[WebSocket()]
[HttpGet]
public IEnumerable<WeatherForecast> Get()
{
    var rng = new Random();
    return Enumerable.Range(1, 2).Select(index => new WeatherForecast
    {
         Date = DateTime.Now.AddDays(index),
         TemperatureC = rng.Next(-20, 55),
         Summary = Summaries[rng.Next(Summaries.Length)]
    }).ToArray();
}
```

## Request and Response

> Scheme namespace 👇  
> Request Cyaim.WebSocketServer.Infrastructure.Handlers.MvcRequestScheme  
> Response Cyaim.WebSocketServer.Infrastructure.Handlers.MvcResponseScheme  

> Request target ignore case

> Request scheme  
### 1. Nonparametric method request
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
	"Target": "WeatherForecast.Get"
	"Status": 0,
	"Msg": null,
	"RequestTime": 637395762382112345,
	"CompleteTime": 637395762382134526,
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
	}]
}
```
Forward invoke method return content will write MvcResponseScheme.Body.  

### 2. Request with parameters  
Example Code:
1. Change method code to:
```C#
[WebSocket]
[HttpGet]
public IEnumerable<WeatherForecast> Get(Test a)
{
    var rng = new Random();
    return Enumerable.Range(1, 2).Select(index => new WeatherForecast
    {
         TemperatureC = a.PreTemperatureC + rng.Next(-20, 55),
         Summary = a.PreSummary + Summaries[rng.Next(Summaries.Length)]
    }).ToArray();
}
```

2. Define parameter class
```C#
public class Test
{
    public string PreSummary { get; set; }
    public int PreTemperatureC { get; set; }
}
```

> Request parameter  
```json
{
	"target": "WeatherForecast.Get",
	"body": {
	    "PreSummary":"Cyaim_",
	    "PreTemperatureC":233
	}
}
```
Request body will write invoke method parameter.
  
  
> Response to this request  
```json
{
	"Target": "WeatherForecast.Get"
	"Status": 0,
	"Msg": null,
	"RequestTime": 0,
	"CompleteTime": 637395922139434966,
	"Body": [{
		"Date": "0001-01-01T00:00:00",
		"TemperatureC": 282,
		"TemperatureF": 539,
		"Summary": "Cyaim_Warm"
	}, {
		"Date": "0001-01-01T00:00:00",
		"TemperatureC": 285,
		"TemperatureF": 544,
		"Summary": "Cyaim_Sweltering"
	}]
}
```
