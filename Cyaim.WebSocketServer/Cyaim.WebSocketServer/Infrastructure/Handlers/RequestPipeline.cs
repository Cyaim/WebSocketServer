using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    /// <summary>
    /// Per-message context that flows through the WebSocket middleware chain.
    /// 流经 WebSocket 中间件链的每消息上下文。
    /// </summary>
    /// <remarks>
    /// 与旧的池化上下文不同，本类型是每条消息新建的普通对象，可以安全地在异步续体中捕获与保存。
    /// Unlike the old pooled context, this is a fresh per-message object and is safe to capture in
    /// async continuations.
    /// </remarks>
    public sealed class WebSocketMessageContext
    {
        /// <summary>HTTP context of the connection / 连接的 HTTP 上下文</summary>
        public HttpContext HttpContext { get; init; }

        /// <summary>The WebSocket instance / WebSocket 实例</summary>
        public WebSocket WebSocket { get; init; }

        /// <summary>Route options / 路由选项</summary>
        public WebSocketRouteOption Options { get; init; }

        /// <summary>WebSocket message type of the inbound message / 入站消息的 WebSocket 消息类型</summary>
        public WebSocketMessageType MessageType { get; init; }

        /// <summary>Ticks when the message started being processed / 开始处理该消息的时间戳</summary>
        public long RequestTimeTicks { get; init; }

        /// <summary>
        /// Raw inbound message bytes (valid length only). Do not retain past the middleware chain.
        /// 入站消息的原始字节（仅有效长度）。不要在中间件链之外保留其引用。
        /// </summary>
        public ReadOnlyMemory<byte> ReceivedData { get; init; }

        /// <summary>Parsed request scheme (Id/Target/Body) / 解析后的请求（Id/Target/Body）</summary>
        public MvcRequestScheme Request { get; set; }

        /// <summary>Parsed request body as a JSON object / 解析后的请求体（JSON 对象）</summary>
        public JsonObject RequestBody { get; set; }

        /// <summary>
        /// The response object produced by the terminal endpoint (or a short-circuiting middleware).
        /// After the chain returns it is serialized and sent, unless <see cref="SuppressResponse"/> is true.
        /// 由终结点（或短路中间件）产生的响应对象；链返回后会被序列化并发送，除非 <see cref="SuppressResponse"/> 为 true。
        /// </summary>
        public object Response { get; set; }

        /// <summary>
        /// Set true so the framework does not send a response for this message (e.g. a middleware
        /// already wrote to the socket itself). / 设为 true 则框架不再为本消息发送响应。
        /// </summary>
        public bool SuppressResponse { get; set; }

        private IDictionary<object, object> _items;

        /// <summary>
        /// Scratch state shared across middleware for this message (lazily created).
        /// 本消息内各中间件共享的临时状态（惰性创建）。
        /// </summary>
        public IDictionary<object, object> Items => _items ??= new Dictionary<object, object>();
    }

    /// <summary>
    /// A terminal or composed step of the WebSocket request pipeline.
    /// WebSocket 请求管道的终结点或组合步骤。
    /// </summary>
    public delegate Task WebSocketRequestDelegate(WebSocketMessageContext context);

    /// <summary>
    /// A WebSocket middleware. Call <paramref name="next"/> to invoke the rest of the pipeline
    /// (including the endpoint); skip it to short-circuit. Run code before and/or after <c>next</c>
    /// to wrap the request.
    /// WebSocket 中间件。调用 <paramref name="next"/> 执行下游（含终结点）；不调用则短路。
    /// 在 <c>next</c> 前后执行代码即可环绕请求。
    /// </summary>
    public interface IWebSocketMiddleware
    {
        /// <summary>Process the message, optionally invoking the rest of the pipeline via <paramref name="next"/>.</summary>
        Task InvokeAsync(WebSocketMessageContext context, WebSocketRequestDelegate next);
    }

    /// <summary>
    /// Extension helpers for registering WebSocket middleware on a <see cref="WebSocketRouteOption"/>.
    /// 在 <see cref="WebSocketRouteOption"/> 上注册 WebSocket 中间件的扩展方法。
    /// </summary>
    public static class WebSocketMiddlewareExtensions
    {
        /// <summary>
        /// Register an inline middleware. Call <c>next(ctx)</c> to continue the pipeline.
        /// 注册一个内联中间件。调用 <c>next(ctx)</c> 继续管道。
        /// </summary>
        public static WebSocketRouteOption Use(this WebSocketRouteOption options, Func<WebSocketMessageContext, WebSocketRequestDelegate, Task> middleware)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (middleware == null) throw new ArgumentNullException(nameof(middleware));
            options.AddMiddleware(next => context => middleware(context, next));
            return options;
        }

        /// <summary>Register a middleware instance. / 注册一个中间件实例。</summary>
        public static WebSocketRouteOption Use(this WebSocketRouteOption options, IWebSocketMiddleware middleware)
        {
            if (middleware == null) throw new ArgumentNullException(nameof(middleware));
            return options.Use(middleware.InvokeAsync);
        }
    }
}
