using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    public class WebSocketRouteOption
    {
        /// <summary>
        /// 依赖注入
        /// </summary>
        public static IServiceProvider ApplicationServices { get; set; }

        /// <summary>
        /// 注入的HttpContext属性名,注入类型：HttpContext
        /// </summary>
        public string InjectionHttpContextPropertyName { get; set; } = "WebSocketHttpContext";

        /// <summary>
        /// 注入的WebSocket属性名,注入类型：WebSocket
        /// </summary>
        public string InjectionWebSocketClientPropertyName { get; set; } = "WebSocketClient";

        /// <summary>
        /// 频道处理程序
        /// </summary>
        public Dictionary<string, WebSocketChannelHandler> WebSocketChannels { get; set; }

        /// <summary>
        /// 监听程序集上下文
        /// </summary>
        public WatchAssemblyContext WatchAssemblyContext { get; set; }

        /// <summary>
        /// 监听程序集路径
        /// </summary>
        public string WatchAssemblyPath { get; set; }

        /// <summary>
        /// 频道处理程序
        /// </summary>
        /// <param name="context">Http上下文</param>
        /// <param name="webSocketManager">Http请求中WebSocket</param>
        /// <param name="logger">日志</param>
        /// <returns></returns>

        public delegate Task WebSocketChannelHandler(HttpContext context, WebSocketManager webSocketManager, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions);


    }
}
