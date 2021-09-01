﻿using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    public interface IWebSocketHandler
    {
        /// <summary>
        /// Handler metadata
        /// </summary>
        WebSocketHandlerMetadata Metadata { get; }

        /// <summary>
        /// Text transfer buffer size
        /// </summary>
        int ReceiveTextBufferSize { get; set; }

        /// <summary>
        /// Binary transfer buffer size
        /// </summary>
        int ReceiveBinaryBufferSize { get; set; }

        /// <summary>
        /// Connection request entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="logger"></param>
        /// <param name="webSocketOptions"></param>
        /// <returns></returns>
        Task ConnectionEntry(HttpContext context, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions);

    }

    /// <summary>
    /// WebSocketHandler describe
    /// </summary>
    public class WebSocketHandlerMetadata
    {
        /// <summary>
        /// Describe the function of the handle and how to use it 
        /// </summary>
        public string Describe { get; set; }

        /// <summary>
        /// This handle allows binary to be transferred
        /// </summary>
        public bool CanHandleBinary { get; set; }

        /// <summary>
        /// This handle allows text to be transferred
        /// </summary>
        public bool CanHandleText { get; set; }
    }
}
