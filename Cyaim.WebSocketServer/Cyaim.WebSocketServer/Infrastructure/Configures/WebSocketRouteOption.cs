using Cyaim.WebSocketServer.Infrastructure.Injectors;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
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
        /// Endpoint 注入器工厂（用于优化注入性能，支持源代码生成和反射两种方式）
        /// </summary>
        internal EndpointInjectorFactory InjectorFactory { get; set; }

        /// <summary>
        /// 方法调用器工厂（用于优化方法调用性能，支持源代码生成和反射两种方式）
        /// </summary>
        internal MethodInvokerFactory MethodInvokerFactory { get; set; }

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
        /// Maximum connection limit, but it will not overwrite the configuration of Kestrel.
        /// How to configure Kestrel? Please read:https://learn.microsoft.com/zh-cn/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-8.0#maximum-client-connections
        /// </summary>
        public ulong? MaxConnectionLimit { get; set; }

        /// <summary>
        /// true if the target requested by each websocket will wait for processing to complete, false if the parallel processing of targets for Websocket requests
        /// </summary>
        public bool EnableForwardTaskSyncProcessingMode { get; set; }

        /// <summary>
        /// Limit the number of tasks forwarded by each connection. If null, it means unrestricted
        /// </summary>
        public uint? MaxConnectionParallelForwardLimit { get; set; }

        /// <summary>
        /// Limit the number of tasks forwarded by each endpoint.
        /// Key: EndPoint Name, Value: SemaphoreSlim
        /// </summary>
        public ConcurrentDictionary<string, SemaphoreSlim> MaxEndPointParallelForwardLimit { get; set; }

        /// <summary>
        /// 接收请求体限速策略配置
        /// </summary>
        public BandwidthLimitPolicy BandwidthLimitPolicy { get; set; }

        /// <summary>
        /// 是否要求请求必须包含Id属性。如果为true，未包含Id的请求将被拒绝响应。
        /// 默认为true，因为客户端需要Id来区分响应来源。
        /// </summary>
        public bool RequireRequestId { get; set; } = true;

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
        public virtual async Task<bool> OnBeforeConnection(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            if (BeforeConnectionEvent != null)
            {
                return await BeforeConnectionEvent(context, webSocketOptions, channel, logger).ConfigureAwait(false);
            }
            return true;
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
        /// Disconnected Event entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public virtual async Task OnDisconnected(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            if (DisconnectedEvent != null)
            {
                await DisconnectedEvent(context, webSocketOptions, channel, logger).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Call when an exception occurs during forwarding to the target
        /// </summary>
        /// <param name="exception">Abnormalities occurring internally</param>
        /// <param name="exceptionResponse">Abnormal response, if there is no need to respond to client information, pass null</param>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel">Channel of occurrence</param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public delegate Task<Handlers.MvcHandler.MvcResponseScheme> ExceptionHandler(Exception exception, Handlers.MvcHandler.MvcRequestScheme request, Handlers.MvcHandler.MvcResponseScheme exceptionResponse, HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger);

        /// <summary>
        /// Call when an exception occurs during forwarding to the target
        /// </summary>
        public event ExceptionHandler ExceptionEvent;

        /// <summary>
        /// Target exception occurred entry
        /// </summary>
        /// <param name="exception">Target exception occurred</param>
        /// <param name="request">Request Body</param>
        /// <param name="exceptionResponse">Abnormal response to client content</param>
        /// <param name="context">Abnormal HttpContext</param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel">Channel with abnormal occurrence</param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public virtual Task<Handlers.MvcHandler.MvcResponseScheme> OnException(Exception exception, Handlers.MvcHandler.MvcRequestScheme request, Handlers.MvcHandler.MvcResponseScheme exceptionResponse, HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            if (ExceptionEvent != null)
            {
                return ExceptionEvent(exception, request, exceptionResponse, context, webSocketOptions, channel, logger);
            }
            return Task.FromResult(exceptionResponse);
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