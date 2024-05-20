using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

// ReSharper disable ClassWithVirtualMembersNeverInherited.Global

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    /// <summary>
    /// WebSocketRoute run parameter
    /// </summary>
    public class WebSocketRouteOption
    {
        /// <summary>
        /// Dependency injection container provider,Set on UseWebSocketServer
        /// </summary>
        public static IServiceProvider ApplicationServices { get; set; }

        /// <summary>
        /// Kestrel server addresses,Set on UseWebSocketServer
        /// </summary>
        public static List<string> ServerAddresses { get; set; }

        /// <summary>
        /// Dependency injection container
        /// </summary>
        public IServiceCollection ApplicationServiceCollection { get; set; }

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
        /// Assembly prefix for Watch [WebSocket],Default:The Controllers folder of this assembly.
        /// </summary>
        public string WatchAssemblyNamespacePrefix { get; set; }

        /// <summary>
        /// Current ASPNETCORE_ENVIRONMENT==Development
        /// </summary>
        public bool IsDevelopment { get; set; } = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";

        /// <summary>
        /// Maximum receive data limit per request,byte count
        /// </summary>
        public long? MaxRequestReceiveDataLimit { get; set; }

        /// <summary>
        /// true if the all identical IDs are allowed to connect and forward, false if the only one connection with the same Connection id is allowed and forwarded
        /// </summary>
        public bool AllowSameConnectionIdAccess { get; set; } = true;

        /// <summary>
        /// Maximum connection limit
        /// </summary>
        public ulong? MaxConnectionLimit { get; set; }

        #region Event

        /// <summary>
        /// Channel handler
        /// </summary>
        /// <param name="context">Http context</param>
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
        /// Close Connected handler
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public delegate Task DisconnectedHandler(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger);

        /// <summary>
        /// Close Connected call
        /// </summary>
        public event DisconnectedHandler DisconnectedEvent;

        /// <summary>
        /// DisConnectedEvent entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public virtual Task OnDisconnected(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            if (DisconnectedEvent != null)
            {
                return DisconnectedEvent(context, webSocketOptions, channel, logger);
            }
            return Task.CompletedTask;
        }

        #endregion

        #region System.Text.Json Options

        /// <summary>
        /// JsonSerializerOptions
        /// </summary>
        public JsonSerializerOptions DefaultRequestJsonSerializerOptions { get; set; } = new JsonSerializerOptions
        {
            // 设置为 true 以忽略属性名称的大小写
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        /// <summary>
        /// JsonSerializerOptions
        /// </summary>
        public JsonSerializerOptions DefaultResponseJsonSerializerOptions { get; set; } = new JsonSerializerOptions
        {
            // 设置为 true 以忽略属性名称的大小写
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        #endregion
    }
}