using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    /// <summary>
    /// Request handler from pipeline
    /// </summary>
    public class RequestPipeline
    {
        public RequestPipeline(RequestPipelineDelegate invoke)
        {
            Invoke = invoke;
        }

        public RequestPipeline()
        {
        }

        public delegate Task RequestPipelineDelegate(HttpContext context, WebSocketRouteOption webSocketOptions);

        public RequestPipelineDelegate Invoke { get; set; }

    }

    /// <summary>
    /// Request handler from pipeline
    /// </summary>
    public class RequestReceivePipeline : RequestPipeline
    {
        public RequestReceivePipeline(RequestPipelineDelegate invoke)
        {
            Invoke = invoke;
        }

        public RequestReceivePipeline()
        {
        }

        public new delegate Task RequestPipelineDelegate(HttpContext context, WebSocket webSocket, WebSocketReceiveResult receiveResult, byte[] data);

        public new RequestPipelineDelegate Invoke { get; set; }
    }

    /// <summary>
    /// Request handler from pipeline
    /// </summary>
    public class RequestForwardPipeline : RequestPipeline
    {
        public RequestForwardPipeline(RequestPipelineDelegate invoke)
        {
            Invoke = invoke;
        }

        public new delegate Task RequestPipelineDelegate(HttpContext context, WebSocket webSocket, WebSocketReceiveResult receiveResult, byte[] data, MvcRequestScheme request, JsonObject requestBody);

        public new RequestPipelineDelegate Invoke { get; set; }
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
        public static ConcurrentQueue<PipelineItem> AddRequestMiddleware(this ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>> pipeline, PipelineItem handler)
        {
            if (!pipeline.TryGetValue(handler.Stage, out ConcurrentQueue<PipelineItem> value))
            {
                value = new ConcurrentQueue<PipelineItem>();
                pipeline.TryAdd(handler.Stage, value);
            }
            value.Enqueue(handler);

            return value;
        }
    }
}
