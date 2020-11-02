using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Middlewares
{
    /// <summary>
    /// WebSocket Route Middleware
    /// </summary>
    public class WebSocketRouteMiddleware
    {
        private readonly RequestDelegate _next;

        private ILogger<WebSocketRouteMiddleware> Logger { get; }

        private readonly WebSocketRouteOption _webSocketOptions;

        /// <summary>
        /// WebSocketRoute Middleware
        /// </summary>
        /// <param name="next"></param>
        /// <param name="logger"></param>
        /// <param name="webSocketOptions"></param>
        public WebSocketRouteMiddleware(RequestDelegate next, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions)
        {
            _next = next;
            Logger = logger;
            _webSocketOptions = webSocketOptions;
        }

        /// <summary>
        /// Middleware call
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task Invoke(HttpContext context)
        {
            bool hasHandler = _webSocketOptions.WebSocketChannels.TryGetValue(context.Request.Path, out var handler);
            if (hasHandler)
            {
                await handler(context, Logger, _webSocketOptions);
                return;
            }
            await _next(context);
        }
    }
}
