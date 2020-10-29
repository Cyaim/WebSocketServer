using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.AspNetCore.Builder;
using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Middlewares
{
    public static class WebSocketRouteMiddlewareExtensions
    {
        public static IApplicationBuilder UseWebSocketRoute(this IApplicationBuilder app, IServiceProvider serviceProvider)
        {
            app.UseMiddleware<WebSocketRouteMiddleware>();

            WebSocketRouteOption.ApplicationServices = serviceProvider;
            return app;
        }
    }
}
