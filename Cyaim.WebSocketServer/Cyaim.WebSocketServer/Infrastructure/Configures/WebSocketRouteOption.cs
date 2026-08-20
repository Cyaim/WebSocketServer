using Cyaim.WebSocketServer.Infrastructure.Handlers;
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

        #region Middleware pipeline / 中间件管道

        /// <summary>
        /// Registered middleware components (outermost first). Each wraps the next delegate.
        /// 已注册的中间件组件（最外层在前），每个包裹下一个委托。
        /// </summary>
        private readonly List<Func<WebSocketRequestDelegate, WebSocketRequestDelegate>> _middleware
            = new List<Func<WebSocketRequestDelegate, WebSocketRequestDelegate>>();

        /// <summary>
        /// Register a middleware component. Called by <c>WebSocketMiddlewareExtensions.Use(...)</c>.
        /// 注册一个中间件组件（由 <c>WebSocketMiddlewareExtensions.Use(...)</c> 调用）。
        /// </summary>
        internal void AddMiddleware(Func<WebSocketRequestDelegate, WebSocketRequestDelegate> component)
        {
            _middleware.Add(component);
        }

        /// <summary>Number of registered middleware / 已注册的中间件数量</summary>
        public int MiddlewareCount => _middleware.Count;

        /// <summary>
        /// Fold the registered middleware around a terminal delegate into a single compiled delegate.
        /// The result is cached by the caller and reused for every message (compile once, zero per-message overhead).
        /// 将已注册的中间件围绕终结点委托折叠成单个编译好的委托；调用方缓存并对每条消息复用（一次编译、每消息零开销）。
        /// </summary>
        /// <param name="terminal">The terminal step (the actual endpoint dispatch) / 终结点步骤（实际的端点分发）</param>
        public WebSocketRequestDelegate BuildPipeline(WebSocketRequestDelegate terminal)
        {
            if (terminal == null) throw new ArgumentNullException(nameof(terminal));
            var app = terminal;
            // Apply in reverse so the first-registered middleware ends up outermost.
            for (int i = _middleware.Count - 1; i >= 0; i--)
            {
                app = _middleware[i](app) ?? throw new InvalidOperationException("A middleware component returned null.");
            }
            return app;
        }

        #endregion

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
        /// Maximum receive data limit per request, in bytes. Defaults to 4 MiB as an OOM/DoS safety cap;
        /// a single (multi-frame) message exceeding this is rejected. Set to null for unlimited (accepts
        /// the OOM risk). Raise it if your app legitimately sends larger messages.
        /// 单条请求最大接收字节数，默认 4 MiB 作为 OOM/DoS 安全上限；超过则拒绝该(多帧)消息。
        /// 设为 null 表示不限(自担 OOM 风险)；如确需更大消息请调高。
        /// </summary>
        public long? MaxRequestReceiveDataLimit { get; set; } = 4L * 1024 * 1024;

        /// <summary>
        /// Process-wide budget (bytes) for the total in-flight multi-frame receive buffers across ALL
        /// connections (defence-in-depth against a coordinated burst of large messages OOM-ing the host).
        /// Null/&lt;=0 disables it (default). When set, a frame that would push the global total over this
        /// budget is rejected with a size-limit log. Single-frame messages are unaffected.
        /// 所有连接在途多帧接收缓冲的进程级总预算(字节)——防止大量大消息同时到达把主机内存打爆。默认 null(禁用)。
        /// 设置后，会使全局总量超预算的帧将被拒绝并记日志。单帧消息不受影响。
        /// </summary>
        public long? MaxTotalReceiveBufferBytes { get; set; }

        /// <summary>
        /// Largest payload the send path will buffer in memory before writing any frame, in bytes.
        /// Defaults to 4 MiB, symmetric with <see cref="MaxRequestReceiveDataLimit"/>. Null = unlimited.
        /// 发送前先在内存中物化的最大载荷字节数，默认 4 MiB，与接收侧上限对称。null 表示不限。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 物化是「绝不发出截断消息」的手段：整条载荷读进内存后再写第一帧，读源失败时一帧都还没发，
        /// 调用方拿到异常而连接毫发无伤。超过这个上限的载荷改走流式（见
        /// <see cref="AllowChunkedSendAboveMaterializeLimit"/>），代价是中途失败只能终结连接。
        /// Materialising is how the send path guarantees it never emits a truncated message: the whole
        /// payload is read into memory before the first frame goes out, so a failing source costs the
        /// caller an exception and leaves the connection untouched. Larger payloads fall back to
        /// streaming, where a mid-send failure can only be answered by terminating the connection.
        /// </para>
        /// <para>
        /// <b>只作用于 Stream 重载。</b>调用方直接交进来的 buffer 本来就在内存里，不受此上限约束。
        /// <b>Applies to the Stream overloads only.</b> A buffer handed in by the caller is already in
        /// memory and is never subject to this cap.
        /// </para>
        /// </remarks>
        public long? MaxSendMaterializeBytes { get; set; } = 4L * 1024 * 1024;

        /// <summary>
        /// Process-wide budget (bytes) for payloads being materialised across ALL connections at once.
        /// Defaults to 256 MiB; null/&lt;=0 disables it. 所有连接同时物化的进程级总预算，默认 256 MiB。
        /// </summary>
        /// <remarks>
        /// 超预算时**降级为流式而不是排队等待**：等待一个可能永远不释放的发送预算会把健康连接饿死。
        /// 默认开启，是因为单条消息的上限管不住并发：峰值物化内存等于「并发发送数 × 单条上限」，
        /// 不设总量上界时它没有任何天花板。超预算只是改走流式，对调用方不可见、不会失败。
        /// Over budget the send degrades to streaming rather than queueing: waiting on a budget that a
        /// stalled peer may never release would starve healthy connections. It is on by default because the
        /// per-message limit cannot bound concurrency — peak materialised memory is "concurrent sends ×
        /// per-message limit", which has no ceiling without this. Exceeding it merely switches to streaming:
        /// invisible to the caller and never a failure.
        /// </remarks>
        public long? MaxTotalSendMaterializeBytes { get; set; } = 256L * 1024 * 1024;

        /// <summary>
        /// Largest single WebSocket frame the send path will write, in bytes. 0 = never split.
        /// Default 256 KiB minus 16. 单个 WebSocket 帧的最大字节数，0 表示不切分。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 切帧与「消息是否完整」无关——多帧发送的唯一失败是写失败，而写失败一律终结连接，
        /// 产生不了带 <c>endOfMessage:true</c> 的短消息。它守的是另外三件事：扇出时的峰值内存、
        /// 带外控制帧（如 Close）的排队延迟、以及取消所暴露的窗口。
        /// Framing is unrelated to message completeness — the only way a multi-frame send fails is a write
        /// failure, and that always terminates the connection, so it cannot produce a short message
        /// carrying <c>endOfMessage:true</c>. It bounds three other things: peak memory during fan-out,
        /// how long an out-of-band control frame (a Close, say) waits behind a payload, and the window a
        /// cancellation is exposed to.
        /// </para>
        /// <para>
        /// 减 16 是为了对齐 <see cref="System.Buffers.ArrayPool{T}"/> 的分桶：底层实现按
        /// 「载荷 + 帧头(最多 14 字节)」租借，取 2^n 会让租借落到 2^(n+1) 桶而白占一倍。
        /// The minus 16 aligns with ArrayPool bucketing: the implementation rents "payload + header (up to
        /// 14 bytes)", so a clean 2^n would land in the 2^(n+1) bucket and waste half of it.
        /// </para>
        /// </remarks>
        public int MaxSendFrameBytes { get; set; } = 256 * 1024 - 16;

        /// <summary>
        /// Whether a payload over <see cref="MaxSendMaterializeBytes"/> falls back to streaming (default)
        /// or is refused outright. 超过物化上限的载荷是降级流式(默认)还是直接拒绝。
        /// </summary>
        /// <remarks>
        /// 降级保住了「能发任意大的流」这个能力，代价是这条路径上中途失败只能终结连接。
        /// 设为 false 则超限一律抛 <c>WebSocketMessageTooLargeException</c>，让调用方自己分块。
        /// The fallback preserves the ability to send arbitrarily large streams, at the cost that a
        /// mid-send failure there can only be answered by terminating the connection. Set false to refuse
        /// instead and make the caller chunk at the application level.
        /// </remarks>
        public bool AllowChunkedSendAboveMaterializeLimit { get; set; } = true;

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