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
    /// <summary>
    /// WebSocketRoute run parameter
    /// </summary>
    public class WebSocketRouteOption
    {
        /// <summary>
        /// Dependency injection container
        /// </summary>
        public static IServiceProvider ApplicationServices { get; set; }

        /// <summary>
        /// Injection HttpContext property name,Injection property type: HttpContext
        /// </summary>
        public string InjectionHttpContextPropertyName { get; set; } = "WebSocketHttpContext";

        /// <summary>
        /// Injection WebSocket property name,Injection property type: WebSocket
        /// </summary>
        public string InjectionWebSocketClientPropertyName { get; set; } = "WebSocketClient";

        /// <summary>
        /// Channel handlers
        /// </summary>
        public Dictionary<string, WebSocketChannelHandler> WebSocketChannels { get; set; }

        /// <summary>
        /// Watch assembly context
        /// </summary>
        public WatchAssemblyContext WatchAssemblyContext { get; set; }

        /// <summary>
        /// watch assembly path
        /// </summary>
        public string WatchAssemblyPath { get; set; }

        /// <summary>
        /// Channel handler
        /// </summary>
        /// <param name="context">Http context</param>
        /// <param name="webSocketManager">Http request WebSocket</param>
        /// <param name="logger">logger</param>
        /// <param name="webSocketOptions">WebSocket configure option</param>
        /// <returns></returns>

        public delegate Task WebSocketChannelHandler(HttpContext context, WebSocketManager webSocketManager, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions);


    }
}
