using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.DebugUI.Middlewares
{
   public static class WebSocketServerDebugUIMiddlewareExtensions
    {

        /// <summary>
        /// Add WebSocket debug ui.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        public static IApplicationBuilder UseWebSocketServerUI(this IApplicationBuilder app, string path = "/cyaim/wsdebug")
        {
            // debug ui
            app.Map(path, (appbuilder) =>
            {
                appbuilder.Run(async context =>
                {

                    await context.Response.WriteAsync("<h1>2333333</h1/");
                });
            });

            return app;
        }

    }
}
