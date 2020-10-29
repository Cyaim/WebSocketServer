using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.AspNetCore.Builder;
using System;
using System.Collections.Generic;
using System.Text;

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
        /// <param name="serviceProvider"></param>
        /// <returns></returns>
        public static IApplicationBuilder UseWebSocketRoute(this IApplicationBuilder app, IServiceProvider serviceProvider)
        {
            app.UseMiddleware<WebSocketRouteMiddleware>();

            WebSocketRouteOption.ApplicationServices = serviceProvider;
            return app;
        }
    }
}
