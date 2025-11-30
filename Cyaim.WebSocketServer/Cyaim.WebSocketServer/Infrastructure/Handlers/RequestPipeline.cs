using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.ObjectPool;
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    /// <summary>
    /// PipelineContext 对象池策略
    /// </summary>
    internal class PipelineContextPooledObjectPolicy : IPooledObjectPolicy<PipelineContext>
    {
        public PipelineContext Create()
        {
            return new PipelineContext();
        }

        public bool Return(PipelineContext obj)
        {
            // 清理引用，避免内存泄漏
            obj.HttpContext = null;
            obj.WebSocketOptions = null;
            obj.WebSocket = null;
            obj.ReceiveResult = null;
            obj.Data = null;
            obj.Request = null;
            obj.RequestBody = null;
            return true;
        }
    }

    /// <summary>
    /// 统一的管道上下文，包含所有管道可能需要的参数
    /// Unified pipeline context containing all parameters that pipelines may need
    /// </summary>
    /// <remarks>
    /// <para>此类型使用对象池管理，以减少内存分配和 GC 压力。</para>
    /// <para>⚠️ 重要：不要在异步操作中保存此对象的引用，因为对象会在 InvokeAsync 方法返回后自动归还到池中并被清理。</para>
    /// <para>✅ 如果需要长时间持有上下文数据，请复制所需的数据（如 Request、Data 等）而不是持有整个 context 对象。</para>
    /// </remarks>
    public class PipelineContext
    {
        private static readonly ObjectPool<PipelineContext> _pool = new DefaultObjectPool<PipelineContext>(new PipelineContextPooledObjectPolicy());

        /// <summary>
        /// HTTP 上下文
        /// </summary>
        public HttpContext HttpContext { get; set; }

        /// <summary>
        /// WebSocket 路由选项
        /// </summary>
        public WebSocketRouteOption WebSocketOptions { get; set; }

        /// <summary>
        /// WebSocket 实例
        /// </summary>
        public WebSocket WebSocket { get; set; }

        /// <summary>
        /// WebSocket 接收结果
        /// </summary>
        public WebSocketReceiveResult ReceiveResult { get; set; }

        /// <summary>
        /// 接收到的数据缓冲区
        /// </summary>
        public byte[] Data { get; set; }

        /// <summary>
        /// MVC 请求方案
        /// </summary>
        public MvcRequestScheme Request { get; set; }

        /// <summary>
        /// 请求体（JSON 对象）
        /// </summary>
        public JsonObject RequestBody { get; set; }

        /// <summary>
        /// 创建基础上下文（仅包含 HttpContext 和 WebSocketOptions）
        /// </summary>
        /// <remarks>
        /// 注意：返回的对象来自对象池，使用完毕后会自动归还，无需手动管理。
        /// </remarks>
        public static PipelineContext CreateBasic(HttpContext context, WebSocketRouteOption options)
        {
            var ctx = _pool.Get();
            ctx.HttpContext = context;
            ctx.WebSocketOptions = options;
            return ctx;
        }

        /// <summary>
        /// 创建接收数据上下文
        /// </summary>
        /// <remarks>
        /// 注意：返回的对象来自对象池，使用完毕后会自动归还，无需手动管理。
        /// </remarks>
        public static PipelineContext CreateReceive(HttpContext context, WebSocket webSocket, WebSocketReceiveResult result, byte[] data, WebSocketRouteOption options = null)
        {
            var ctx = _pool.Get();
            ctx.HttpContext = context;
            ctx.WebSocket = webSocket;
            ctx.ReceiveResult = result;
            ctx.Data = data;
            ctx.WebSocketOptions = options;
            return ctx;
        }

        /// <summary>
        /// 创建转发数据上下文
        /// </summary>
        /// <remarks>
        /// 注意：返回的对象来自对象池，使用完毕后会自动归还，无需手动管理。
        /// </remarks>
        public static PipelineContext CreateForward(HttpContext context, WebSocket webSocket, WebSocketReceiveResult result, byte[] data, MvcRequestScheme request, JsonObject requestBody, WebSocketRouteOption options = null)
        {
            var ctx = _pool.Get();
            ctx.HttpContext = context;
            ctx.WebSocket = webSocket;
            ctx.ReceiveResult = result;
            ctx.Data = data;
            ctx.Request = request;
            ctx.RequestBody = requestBody;
            ctx.WebSocketOptions = options;
            return ctx;
        }

        /// <summary>
        /// 归还对象到池中（使用完上下文后调用）
        /// </summary>
        /// <remarks>
        /// 注意：此方法通常由框架自动调用，无需手动调用。
        /// 对象归还后会被清理并重用，因此不应在异步操作中保存此对象的引用。
        /// 如果需要长时间持有上下文数据，请复制所需的数据而不是持有整个 context 对象。
        /// </remarks>
        public void Return()
        {
            _pool.Return(this);
        }
    }

    /// <summary>
    /// 请求管道处理器基类
    /// </summary>
    public abstract class RequestPipeline
    {
        /// <summary>
        /// 统一的管道调用方法
        /// </summary>
        /// <param name="context">管道上下文</param>
        /// <remarks>
        /// <para>重要提示：PipelineContext 使用对象池管理，会在 InvokeAsync 方法返回后自动归还到池中。</para>
        /// <para>⚠️ 不要在异步操作中保存 context 的引用，因为方法返回后对象会被清理和重用。</para>
        /// <para>✅ 如果需要长时间持有上下文数据，请复制所需的数据（如 context.Request、context.Data 等）而不是持有整个 context 对象。</para>
        /// <para>✅ 示例：var requestCopy = context.Request; var dataCopy = context.Data?.ToArray();</para>
        /// </remarks>
        public abstract Task InvokeAsync(PipelineContext context);
    }

    /// <summary>
    /// 基于委托的管道处理器实现，方便快速创建管道
    /// </summary>
    public class DelegateRequestPipeline : RequestPipeline
    {
        private readonly Func<PipelineContext, Task> _handler;

        /// <summary>
        /// 创建基于委托的管道处理器
        /// </summary>
        /// <param name="handler">管道处理委托</param>
        public DelegateRequestPipeline(Func<PipelineContext, Task> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// 执行管道处理
        /// </summary>
        /// <remarks>
        /// 注意：context 对象会在方法返回后自动归还到对象池，不要在委托中保存 context 的引用。
        /// 如需长时间持有数据，请复制所需的数据。
        /// </remarks>
        public override Task InvokeAsync(PipelineContext context)
        {
            return _handler(context);
        }
    }

    /// <summary>
    /// Pipeline item
    /// </summary>
    public class PipelineItem
    {
        /// <summary>
        /// Pipeline delegation
        /// </summary>
        public RequestPipeline Item { get; set; }

        /// <summary>
        /// Request pipeline stage
        /// </summary>
        public RequestPipelineStage Stage { get; set; }

        /// <summary>
        /// The order in the pipeline, from small to large
        /// </summary>
        public float Order { get; set; }

        /// <summary>
        /// Exception occurred when calling the pipeline
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// Exception pipeline objects that occurred
        /// </summary>
        public PipelineItem ExceptionItem { get; set; }
    }

    /// <summary>
    /// Request stage
    /// </summary>
    public enum RequestPipelineStage
    {
        /// <summary>
        /// Before receiving data
        /// 接收数据前
        /// </summary>
        BeforeReceivingData,
        /// <summary>
        /// Receiving data
        /// 接收数据中
        /// </summary>
        ReceivingData,
        /// <summary>
        /// After receiving data
        /// 接收数据后
        /// </summary>
        AfterReceivingData,
        /// <summary>
        /// Before forwarding data
        /// 转发数据前
        /// </summary>
        BeforeForwardingData,
        /// <summary>
        /// After forwarding data
        /// 转发数据后
        /// </summary>
        AfterForwardingData,
        /// <summary>
        /// Connected
        /// 客户端已连接
        /// </summary>
        Connected,
        /// <summary>
        /// Disconnected
        /// 客户端断开连接
        /// </summary>
        Disconnected
    }


    public static class RequestPipelineMiddlewareExtensions
    {
        /// <summary>
        /// Add request middleware to RequestPipeline
        /// </summary>
        /// <param name="pipeline">Pipeline</param>
        /// <param name="handler">Processing program</param>
        /// <returns></returns>
        public static ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>> AddRequestMiddleware(this ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>> pipeline, PipelineItem handler)
        {
            if (!pipeline.TryGetValue(handler.Stage, out ConcurrentQueue<PipelineItem> value))
            {
                value = new ConcurrentQueue<PipelineItem>();
                pipeline.TryAdd(handler.Stage, value);
            }
            value.Enqueue(handler);

            return pipeline;
        }

        /// <summary>
        /// Add request middleware to RequestPipeline
        /// </summary>
        /// <param name="pipeline"></param>
        /// <param name="stage"></param>
        /// <param name="invoke"></param>
        /// <param name="order">If it is null, add 1 on the largest order in the current stage queue. If there is no data in the current queue, the order is 0.</param>
        /// <returns></returns>
        public static ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>> AddRequestMiddleware(this ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>> pipeline, RequestPipelineStage stage, RequestPipeline invoke, float? order = null)
        {
            if (!pipeline.TryGetValue(stage, out ConcurrentQueue<PipelineItem> value))
            {
                value = new ConcurrentQueue<PipelineItem>();
                pipeline.TryAdd(stage, value);
            }
            value.Enqueue(new PipelineItem()
            {
                Item = invoke,
                Order = order ?? 0,
                Stage = stage
            });

            return pipeline;
        }

        /// <summary>
        /// 添加基于委托的管道处理器（便捷方法）
        /// </summary>
        /// <param name="pipeline">管道字典</param>
        /// <param name="stage">管道阶段</param>
        /// <param name="handler">处理委托</param>
        /// <param name="order">执行顺序，如果为 null，则自动递增</param>
        /// <returns></returns>
        public static ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>> AddRequestMiddleware(this ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>> pipeline, RequestPipelineStage stage, Func<PipelineContext, Task> handler, float? order = null)
        {
            var delegatePipeline = new DelegateRequestPipeline(handler);
            return pipeline.AddRequestMiddleware(stage, delegatePipeline, order);
        }
    }
}
