using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Configuration;
using System.Data;
using System.Windows;

namespace Cyaim.WebSocketServer.Example.Wpf
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private IHost? _webHost;
        private MainWindow? _mainWindow;


        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            // 必须在CreateHostBuilder前new，因为配置日志时需要实例
            _mainWindow = new MainWindow();

            _webHost = CreateHostBuilder(e.Args).Build();
            await _webHost.StartAsync();

            _mainWindow!.Show();
        }

        private IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                // 使用UseStartup就不用在这里写
                .ConfigureServices((context, services) =>
                {
                    // 配置WebSocketServer的Handler
                    services.ConfigureWebSocketRoute(x =>
                    {
                        var mvcHandler = new MvcChannelHandler();
                        //Define channels
                        x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
                        {
                            { "/ws", mvcHandler.ConnectionEntry}
                        };
                        //x.WatchAssemblyNamespacePrefix = "可以自定义被扫描特性的完整限定命名空间，默认是本程序集的Controllers";
                        x.ApplicationServiceCollection = services;
                    });
                })
                // 配置日志
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddProvider(new WebSocketLoggerProvider(LogLevel.Information, _mainWindow!.AppendLog));
                })
                // 配置WebHost
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseKestrel()
                              .UseUrls("http://localhost:5000")
                              //.UseStartup<Startup>();

                              // 使用UseStartup就不用在这里写
                              .Configure(app =>
                              {
                                  // 启用Kestral的WebSocket
                                  var webSocketOptions = new WebSocketOptions()
                                  {
                                      KeepAliveInterval = TimeSpan.FromSeconds(120),
                                  };
                                  app.UseWebSockets(webSocketOptions);
                                  // 启用WebSocketServer
                                  app.UseWebSocketServer();
                              });
                });

        protected override async void OnExit(ExitEventArgs e)
        {
            // 释放WebHost
            if (_webHost != null)
            {
                await _webHost.StopAsync(TimeSpan.FromSeconds(5));
                _webHost.Dispose();
            }
            base.OnExit(e);
        }


    }

}
