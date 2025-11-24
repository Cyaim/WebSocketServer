using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cyaim.WebSocketServer.Example.Wpf
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
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

            // 用不上Http
            //services.AddControllers();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            // 启用Kestral的WebSocket
            var webSocketOptions = new WebSocketOptions()
            {
                KeepAliveInterval = TimeSpan.FromSeconds(120),
            };
            app.UseWebSockets(webSocketOptions);
            // 启用WebSocketServer
            app.UseWebSocketServer();

            // 用不上Http
            //app.UseEndpoints(endpoints =>
            //{
            //    endpoints.MapControllers();
            //});
        }
    }
}