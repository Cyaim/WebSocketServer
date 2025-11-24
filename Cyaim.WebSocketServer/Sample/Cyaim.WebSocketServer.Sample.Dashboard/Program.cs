using Cyaim.WebSocketServer.Dashboard.Middlewares;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container / 添加服务到容器
builder.Services.AddControllers();

// Add Dashboard services / 添加 Dashboard 服务
builder.Services.AddWebSocketDashboard();

// Configure WebSocketServer / 配置 WebSocketServer
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    
    // Define WebSocket channels / 定义 WebSocket 通道
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }
    };
    
    x.ApplicationServiceCollection = builder.Services;
});

var app = builder.Build();

// Configure the HTTP request pipeline / 配置 HTTP 请求管道
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseRouting();

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers(); // Dashboard API requires this / Dashboard API 需要这个
});

// Configure WebSocket / 配置 WebSocket
var webSocketOptions = new WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(15)
};

app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();

// Use Dashboard middleware / 使用 Dashboard 中间件
// Must be after MapControllers / 必须在 MapControllers 之后
app.UseWebSocketDashboard("/dashboard");

// Run the application / 运行应用程序
app.Run();

