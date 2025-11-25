using Cyaim.WebSocketServer.Dashboard.Middlewares;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Cyaim.WebSocketServer.Sample.Dashboard.Interfaces;
using Cyaim.WebSocketServer.Sample.Dashboard.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container / 添加服务到容器
builder.Services.AddControllers();

// Add Swagger/OpenAPI services / 添加 Swagger/OpenAPI 服务
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add Dashboard services / 添加 Dashboard 服务
builder.Services.AddWebSocketDashboard();

// Add HTTP client for test API / 为测试 API 添加 HTTP 客户端
builder.Services.AddHttpClient();

// Register WebSocket Cluster Test API / 注册 WebSocket 集群测试 API
builder.Services.AddSingleton<IWebSocketClusterTestApi>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<WebSocketClusterTestApi>>();
    var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient();
    // 设置基础地址为当前应用地址
    var baseUrl = builder.Configuration["TestApi:BaseUrl"] ?? "/api/dashboard";
    return new WebSocketClusterTestApi(logger, httpClient, baseUrl);
});

// Register Cluster Test Service / 注册集群测试服务
builder.Services.AddScoped<ClusterTestService>();

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

    // Enable Swagger UI in development / 在开发环境中启用 Swagger UI
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "WebSocket Cluster Test API v1");
        c.RoutePrefix = "swagger"; // Swagger UI 访问路径: /swagger
        c.DocumentTitle = "WebSocket Cluster Test API";
        c.DefaultModelsExpandDepth(-1); // 隐藏模型定义，使界面更简洁
    });
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

