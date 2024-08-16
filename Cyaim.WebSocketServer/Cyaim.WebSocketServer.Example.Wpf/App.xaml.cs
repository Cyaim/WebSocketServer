using Microsoft.AspNetCore.Hosting;
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
        private IHost _webHost;
        private MainWindow _mainWindow;


        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            // 必须在CreateHostBuilder前new，因为配置日志时需要实例
            _mainWindow = new MainWindow();

            _webHost = CreateHostBuilder(e.Args).Build();
            await _webHost.StartAsync();

            _mainWindow.Show();
        }

        private IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                // 配置日志
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddProvider(new WebSocketLoggerProvider(LogLevel.Information, _mainWindow.AppendLog));
                })
                // 配置WebHost
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseKestrel()
                              .UseUrls("http://localhost:5000")
                              .UseStartup<Startup>();
                });

        protected override async void OnExit(ExitEventArgs e)
        {
            // 释放WebHost
            if (_webHost != null)
            {
                await _webHost.StopAsync(TimeSpan.FromSeconds(5));
                _webHost.Dispose();
                _webHost = null;
            }
            base.OnExit(e);
        }


    }

}
