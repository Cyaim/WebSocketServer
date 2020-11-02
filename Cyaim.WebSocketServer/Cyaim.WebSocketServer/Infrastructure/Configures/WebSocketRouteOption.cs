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
        /// Injection HttpContext property name.
        /// Default property name: WebSocketHttpContext.
        /// Injection property type: HttpContext
        /// </summary>
        public string InjectionHttpContextPropertyName { get; set; } = "WebSocketHttpContext";

        /// <summary>
        /// Injection WebSocket property name.
        /// Default property name: WebSocketClient.
        /// Injection property type: WebSocket
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
        /// Watch assembly path
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

        public delegate Task WebSocketChannelHandler(HttpContext context, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions);

        /// <summary>
        /// Before establish connection handler
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel"></param>
        /// <param name="logger"></param>
        /// <returns>true allow connection,false deny connection</returns>
        public delegate Task<bool> BeforeConnectionHandler(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger);

        /// <summary>
        /// Before establish connection call
        /// </summary>
        public event BeforeConnectionHandler BeforeConnectionEvent;

        /// <summary>
        /// BeforeConnectionEvent entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public virtual Task<bool> OnBeforeConnection(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            if (BeforeConnectionEvent != null)
            {
                return BeforeConnectionEvent(context, webSocketOptions, channel, logger);
            }
            return Task.FromResult(true);
        }


        /// <summary>
        /// Close connectioned handler
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public delegate Task DisConnectionedHandler(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger);

        /// <summary>
        /// Close connectioned call
        /// </summary>
        public event DisConnectionedHandler DisConnectionedEvent;

        /// <summary>
        /// DisConnectionedEvent entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public virtual Task OnDisConnectioned(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            if (DisConnectionedEvent != null)
            {
                return DisConnectionedEvent(context, webSocketOptions, channel, logger);
            }
            return Task.CompletedTask;
        }
    }
}
