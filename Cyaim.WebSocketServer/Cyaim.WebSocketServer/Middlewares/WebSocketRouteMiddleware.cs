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
    public class WebSocketRouteMiddleware
    {
        private readonly RequestDelegate _next;

        private ILogger<WebSocketRouteMiddleware> Logger { get; }

        public readonly WebSocketRouteOption _webSocketOptions;


        public WebSocketRouteMiddleware(RequestDelegate next, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions)
        {
            _next = next;
            Logger = logger;
            _webSocketOptions = webSocketOptions;
        }

        public async Task Invoke(HttpContext context)
        {
            bool hasHandler = _webSocketOptions.WebSocketChannels.TryGetValue(context.Request.Path, out var handler);
            if (hasHandler)
            {
                await handler(context, context?.WebSockets, Logger, _webSocketOptions);
                return;
            }
            await _next(context);
        }
    }
}
