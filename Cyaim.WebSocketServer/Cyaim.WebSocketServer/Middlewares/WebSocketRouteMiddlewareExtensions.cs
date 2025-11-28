using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;

namespace Cyaim.WebSocketServer.Middlewares
{
    /// <summary>
    /// WebSocketRouteMiddleware Extensions
    /// </summary>
    public static class WebSocketRouteMiddlewareExtensions
    {

        /// <summary>
        /// Add Cyaim.WebSocketServer.Infrastructure.Middlewares.WebSocketRouteMiddleware Middleware.
        /// The websocket request will execute the with relation endpoint methods.
        /// </summary>
        /// <param name="app"></param>
        /// <returns></returns>
        public static IApplicationBuilder UseWebSocketServer(this IApplicationBuilder app)
        {
            app.UseMiddleware<WebSocketRouteMiddleware>();
            WebSocketRouteOption.ApplicationServices = app.ApplicationServices;

            return app;
        }

        /// <summary>
        /// Add Cyaim.WebSocketServer.Infrastructure.Middlewares.WebSocketRouteMiddleware Middleware with configuration.
        /// This overload allows you to configure WebSocketRouteOption at middleware setup time,
        /// where you can directly access services from the application's service provider.
        /// The websocket request will execute the with relation endpoint methods.
        /// 
        /// 此重载允许您在中间件设置时配置 WebSocketRouteOption，
        /// 此时您可以直接从应用程序的服务提供者访问服务，无需手动创建 ServiceProvider。
        /// </summary>
        /// <param name="app">The application builder / 应用程序构建器</param>
        /// <param name="configure">Configuration action that receives the WebSocketRouteOption and IServiceProvider / 配置操作，接收 WebSocketRouteOption 和 IServiceProvider</param>
        /// <returns></returns>
        public static IApplicationBuilder UseWebSocketServer(this IApplicationBuilder app, Action<WebSocketRouteOption, IServiceProvider> configure)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }

            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            var serviceProvider = app.ApplicationServices;
            
            // 尝试从服务容器中获取已配置的 WebSocketRouteOption
            var existingOption = serviceProvider.GetService<WebSocketRouteOption>();
            WebSocketRouteOption option;

            if (existingOption != null)
            {
                // 如果已经通过 ConfigureWebSocketRoute 配置过，使用现有的配置并允许补充配置
                option = existingOption;
            }
            else
            {
                // 如果没有配置过，创建新的配置
                // 注意：这种情况下，需要确保在 configure 中完成所有必要的配置
                option = new WebSocketRouteOption();
            }

            // 执行配置操作，传入 option 和 serviceProvider，此时可以直接从 serviceProvider 获取服务
            configure(option, serviceProvider);

            // 验证必要的配置
            if (option.WebSocketChannels == null || option.WebSocketChannels.Count < 1)
            {
                throw new InvalidOperationException("WebSocketRouteOption.WebSocketChannels must be configured. Please configure WebSocketChannels in the configure action, or call ConfigureWebSocketRoute first.");
            }

            // 如果 ApplicationServiceCollection 未设置，尝试从已注册的服务中获取
            // 注意：如果用户完全在 UseWebSocketServer 中配置（没有调用 ConfigureWebSocketRoute），
            // ApplicationServiceCollection 可能为 null，这通常不影响基本功能
            // 但如果需要某些高级功能（如动态服务解析），可能需要先调用 ConfigureWebSocketRoute

            // 如果之前没有注册到服务容器，我们需要通过自定义方式传递配置
            // 由于服务容器已构建，我们使用闭包来传递配置
            if (existingOption == null)
            {
                // 创建一个包装中间件，通过闭包传递配置
                app.Use(next =>
                {
                    var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
                    var logger = loggerFactory.CreateLogger<WebSocketRouteMiddleware>();
                    var middleware = new WebSocketRouteMiddleware(next, logger, option);
                    return middleware.Invoke;
                });
            }
            else
            {
                // 如果已经注册，直接使用标准的中间件注册方式
                app.UseMiddleware<WebSocketRouteMiddleware>();
            }

            WebSocketRouteOption.ApplicationServices = serviceProvider;

            return app;
        }

        /// <summary>
        /// Get WebSocket access address
        /// </summary>
        /// <returns></returns>
        public static void GetWebSocketAddress()
        {
            var server = WebSocketRouteOption.ApplicationServices.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>();
            WebSocketRouteOption.ServerAddresses = address.Addresses.Select(x => x.Replace("https", "wss").Replace("http", "ws")).ToList();
            foreach (var item in WebSocketRouteOption.ServerAddresses)
            {
                Console.WriteLine(I18nText.NowWebSocketOn + item);
            }
        }

        /// <summary>
        /// Use websocket cluster start service.
        /// Add Cyaim.WebSocketServer.Infrastructure.Middlewares.WebSocketRouteMiddleware Middleware.
        /// The websocket request will execute the with relation endpoint methods.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="serviceProvider"></param>
        /// <param name="clusterOption"></param>
        /// <returns></returns>
        public static IApplicationBuilder UseWebSocketServer(this IApplicationBuilder app, IServiceProvider serviceProvider, Action<ClusterOption> clusterOption)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }

            if (serviceProvider == null)
            {
                throw new ArgumentNullException(nameof(serviceProvider));
            }

            if (clusterOption == null)
            {
                throw new ArgumentNullException(nameof(clusterOption));
            }


            ClusterOption cluster = new ClusterOption();
            clusterOption(cluster);
            if (cluster == null)
            {
                throw new ArgumentNullException(nameof(clusterOption));
            }

            WebSocketRouteOption wsro = serviceProvider.GetService(typeof(WebSocketRouteOption)) as WebSocketRouteOption;
            bool hasClusterChannel = wsro.WebSocketChannels.ContainsKey(cluster.ChannelName);
            if (!hasClusterChannel)
            {
                throw new InvalidOperationException($"WebSocket集群 -> WebSocketRouteOption中没有定义集群数据交换通道");
            }

            Console.WriteLine($"WebSocket集群 -> {(cluster.NodeLevel == ServiceLevel.Master ? "主节点" : "从节点")}");

            GlobalClusterCenter.ClusterContext = cluster;
            //foreach (var item in GlobalClusterCenter.ClusterContext.Nodes)
            //{
            //    var exitEvent = new ManualResetEvent(false);
            //    var url = new Uri($"ws://{item}/{cluster.ChannelName}");
            //    var factory = new Func<ClientWebSocket>(() => new ClientWebSocket
            //    {
            //        Options =
            //            {
            //                KeepAliveInterval = TimeSpan.FromSeconds(5),
            //                //ClientCertificates = ...
            //            }
            //    });
            //    using (var client = new WebsocketClient(url, factory))
            //    {
            //        client.ReconnectTimeout = TimeSpan.FromSeconds(10);

            //        client.ReconnectionHappened.Subscribe(info => Console.WriteLine($"Reconnection happened, type: {info.Type}"));

            //        client.MessageReceived.Subscribe(msg => Console.WriteLine($"Message received: {msg}"));
            //        client.Start();

            //        Task.Run(() => client.Send("{ message }"));

            //        exitEvent.WaitOne();
            //    }
            //}



            app.UseMiddleware<WebSocketRouteMiddleware>();
            WebSocketRouteOption.ApplicationServices = serviceProvider;


            return app;
        }
    }
}
