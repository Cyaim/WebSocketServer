using Cyaim.WebSocketServer.Benchmarks;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.MessagePack;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

const int port = 5199;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvc = new MvcChannelHandler();
    var mp = new MessagePackChannelHandler();
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
    {
        { "/ws", mvc.ConnectionEntry },
        { "/mp", mp.ConnectionEntry },
    };
    // A deliberately tight buffered cap — streaming uploads (bench.upload) must bypass it.
    x.MaxRequestReceiveDataLimit = 64 * 1024;
    x.WatchAssemblyNamespacePrefix = "Cyaim.WebSocketServer.Benchmarks.Controllers";
    x.ApplicationServiceCollection = builder.Services;
});

var app = builder.Build();
app.UseWebSockets();
app.UseWebSocketServer();
await app.StartAsync();

Console.WriteLine($"Benchmark server on http://127.0.0.1:{port}  (buffered cap 64 KiB; streaming bypasses it)");
Console.WriteLine();

await BenchmarkRunner.RunAll($"ws://127.0.0.1:{port}");

await app.StopAsync();
