using System;

namespace Cyaim.WebSocketServer.Infrastructure.Attributes
{
    /// <summary>
    /// WebSocket Endpoint mark
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class WebSocketAttribute : Attribute
    {
        /// <summary>
        /// Mark action use action name
        /// </summary>
        public WebSocketAttribute() { }

        /// <summary>
        /// Mark action use method value
        /// </summary>
        /// <param name="method"></param>
        public WebSocketAttribute(string method) : this()
        {
            Method = method;
        }

        /// <summary>
        /// Endpoint method name
        /// </summary>
        public string Method { get; set; }

        /// <summary>
        /// Mark this endpoint as a streaming endpoint. When true the handler does NOT buffer the whole
        /// message in memory; it hands the incoming bytes to the endpoint as a <see cref="System.IO.Stream"/>
        /// (fed frame-by-frame), so memory stays constant regardless of payload size — suitable for file
        /// upload. When false (default) the endpoint uses the normal buffered dispatch.
        /// 标记为流式端点。为 true 时不整体缓冲消息，而是把到达的字节逐帧喂给端点的 Stream 形参，内存恒定、
        /// 与负载大小无关（适合文件上传）。为 false（默认）走普通的缓冲式分发。
        /// </summary>
        public bool Stream { get; set; }

        /// <summary>
        /// Per-endpoint size cap in bytes; 0 (default) means "use the global MaxRequestReceiveDataLimit".
        /// Meaning depends on <see cref="Stream"/>:
        /// - buffered endpoint (Stream=false): the max bytes buffered in memory for one message (方案 A).
        /// - streaming endpoint (Stream=true): the max total bytes streamed for one upload (方案 B),
        ///   enforced as a running counter (not a memory buffer).
        /// 端点级字节上限；0（默认）表示沿用全局 MaxRequestReceiveDataLimit。含义随 Stream 变化：
        /// 缓冲式=单条消息最多缓冲的内存字节（方案 A）；流式=单次上传最多流过的字节数（方案 B，按计数器封顶，不占内存）。
        /// </summary>
        public long MaxBytes { get; set; }
    }
}