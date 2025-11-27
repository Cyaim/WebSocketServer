using Cyaim.WebSocketServer.Dashboard.Middlewares;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Cyaim.WebSocketServer.Sample.Dashboard.Interfaces;
using Cyaim.WebSocketServer.Sample.Dashboard.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// 根据命令行参数加载对应的配置文件
// 例如: dotnet run -- --node node1 或直接运行 exe --node node1
var nodeArg = args.FirstOrDefault(arg => arg.StartsWith("--node="))?.Split('=')[1] 
    ?? args.SkipWhile(arg => arg != "--node").Skip(1).FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("CLUSTER_NODE")
    ?? "node1";

if (nodeArg != null)
{
    var configFile = $"appsettings.{nodeArg}.json";
    if (System.IO.File.Exists(configFile))
    {
        builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: true);
        Console.WriteLine($"已加载配置文件: {configFile}");
    }
}

// Add services to the container / 添加服务到容器
// Include controllers from Dashboard assembly / 包含 Dashboard 程序集的控制器
var dashboardAssembly = typeof(Cyaim.WebSocketServer.Dashboard.Controllers.ClusterController).Assembly;
builder.Services.AddControllers()
    .AddApplicationPart(dashboardAssembly); // 显式添加 Dashboard 程序集的控制器

// Add Swagger/OpenAPI services / 添加 Swagger/OpenAPI 服务
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Include controllers from Dashboard assembly / 包含 Dashboard 程序集的控制器
    var dashboardAssembly = typeof(Cyaim.WebSocketServer.Dashboard.Controllers.ClusterController).Assembly;
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "WebSocket Cluster Test API",
        Version = "v1",
        Description = "WebSocket Server Cluster Test API and Dashboard API"
    });
    
    // Include XML comments if available / 如果可用，包含 XML 注释
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (System.IO.File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }
    
    var dashboardXmlFile = $"{dashboardAssembly.GetName().Name}.xml";
    var dashboardXmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, dashboardXmlFile);
    if (System.IO.File.Exists(dashboardXmlPath))
    {
        c.IncludeXmlComments(dashboardXmlPath);
    }
});

// Add Dashboard services / 添加 Dashboard 服务
builder.Services.AddWebSocketDashboard();

// Add HTTP client for test API / 为测试 API 添加 HTTP 客户端
builder.Services.AddHttpClient();

// Add CORS support for testing / 添加 CORS 支持用于测试
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

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

    // 读取集群配置
    var clusterConfig = builder.Configuration.GetSection("Cluster");
    var clusterChannelName = clusterConfig["ChannelName"] ?? "/cluster";
    
    // Define WebSocket channels / 定义 WebSocket 通道
    // 添加集群通道（如果配置了集群）
    var channels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }
    };
    
    // 如果配置了集群，添加集群通道
    if (clusterConfig.Exists() && !string.IsNullOrEmpty(clusterConfig["NodeId"]))
    {
        // 集群通道使用专门的集群处理器
        channels[clusterChannelName] = Cyaim.WebSocketServer.Infrastructure.Cluster.ClusterChannelHandler.ConnectionEntry;
    }
    
    x.WebSocketChannels = channels;
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

// Enable CORS / 启用 CORS
app.UseCors("AllowAll");

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

// 配置并启动集群（如果配置了集群）
var clusterConfig = app.Configuration.GetSection("Cluster");
if (clusterConfig.Exists() && !string.IsNullOrEmpty(clusterConfig["NodeId"]))
{
    // 在应用启动时初始化集群
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger<Program>();
    
    try
    {
        // 创建集群配置选项
        var clusterOption = new ClusterOption
        {
            NodeId = clusterConfig["NodeId"],
            NodeAddress = clusterConfig["NodeAddress"] ?? "localhost",
            NodePort = clusterConfig.GetValue<int>("NodePort", 0),
            TransportType = clusterConfig["TransportType"] ?? "ws",
            ChannelName = clusterConfig["ChannelName"] ?? "/cluster",
            Nodes = clusterConfig.GetSection("Nodes").Get<string[]>() ?? Array.Empty<string>()
        };

        // 生成节点 ID（如果未配置）
        var nodeId = clusterOption.NodeId ?? Guid.NewGuid().ToString();
        clusterOption.NodeId = nodeId;

        // 创建传输层
        var transport = ClusterTransportFactory.CreateTransport(loggerFactory, nodeId, clusterOption);

        // 创建 Raft 节点
        var raftNode = new RaftNode(
            loggerFactory.CreateLogger<RaftNode>(),
            transport,
            nodeId);

        // 创建集群路由器
        var router = new ClusterRouter(
            loggerFactory.CreateLogger<ClusterRouter>(),
            transport,
            raftNode,
            nodeId);

        // 创建集群管理器
        var clusterManager = new ClusterManager(
            loggerFactory.CreateLogger<ClusterManager>(),
            transport,
            raftNode,
            router,
            nodeId,
            clusterOption);

        // 设置 WebSocket 连接提供者
        var connectionProvider = new DefaultWebSocketConnectionProvider();
        clusterManager.SetConnectionProvider(connectionProvider);
        router.SetConnectionProvider(connectionProvider);

        // 保存到全局中心
        GlobalClusterCenter.ClusterContext = clusterOption;
        GlobalClusterCenter.ClusterManager = clusterManager;
        GlobalClusterCenter.ConnectionProvider = connectionProvider;

        // 在应用启动时启动集群
        lifetime.ApplicationStarted.Register(() =>
        {
            Task.Run(async () =>
            {
                try
                {
                    await clusterManager.StartAsync();
                    logger.LogInformation($"集群节点 {nodeId} 已启动，监听地址: {clusterOption.NodeAddress}:{clusterOption.NodePort}");
                    logger.LogInformation($"集群节点列表: {string.Join(", ", clusterOption.Nodes ?? Array.Empty<string>())}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "启动集群时发生错误");
                }
            });
        });

        // 在应用关闭时停止集群（优雅关闭）
        lifetime.ApplicationStopping.Register(() =>
        {
            Task.Run(async () =>
            {
                try
                {
                    // Graceful shutdown: transfer connections before stopping / 优雅关闭：在停止前转移连接
                    await clusterManager.ShutdownAsync(force: false);
                    logger.LogInformation($"集群节点 {nodeId} 已优雅关闭");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "优雅关闭集群时发生错误，尝试强制关闭");
                    try
                    {
                        // Force shutdown if graceful shutdown fails / 如果优雅关闭失败，强制关闭
                        await clusterManager.ShutdownAsync(force: true);
                    }
                    catch (Exception forceEx)
                    {
                        logger.LogError(forceEx, "强制关闭集群时发生错误");
                    }
                }
            }).Wait(TimeSpan.FromSeconds(30)); // 等待最多30秒（优雅关闭需要更多时间）
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "初始化集群时发生错误");
        // 不抛出异常，允许应用继续运行（单节点模式）
    }
}

// Use Dashboard middleware / 使用 Dashboard 中间件
// Must be after MapControllers / 必须在 MapControllers 之后
app.UseWebSocketDashboard("/dashboard");

// Run the application / 运行应用程序
app.Run();

