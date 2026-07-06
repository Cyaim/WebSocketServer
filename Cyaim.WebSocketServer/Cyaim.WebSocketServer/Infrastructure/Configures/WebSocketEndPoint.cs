using System;
using System.Reflection;

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    /// <summary>
    /// WebSocket endpoint
    /// </summary>
    public class WebSocketEndPoint
    {
        /// <summary>
        /// Controller Name
        /// </summary>
        public string Controller { get; set; }

        /// <summary>
        /// Action of controller
        /// </summary>
        public string Action { get; set; }

        /// <summary>
        /// WebSocket request target
        /// </summary>
        public string MethodPath { get; set; }

        /// <summary>
        /// WebSocket Attribute method name
        /// </summary>
        public string[] Methods { get; set; }

        /// <summary>
        /// Method of action
        /// </summary>
        public MethodInfo MethodInfo { get; set; }

        /// <summary>
        /// Endpoint where class
        /// </summary>
        public Type Class { get; set; }

        /// <summary>
        /// Streaming endpoint (see <see cref="Attributes.WebSocketAttribute.Stream"/>). 流式端点。
        /// </summary>
        public bool IsStream { get; set; }

        /// <summary>
        /// Per-endpoint size cap in bytes (0 = use global). Buffered: max buffered bytes; streaming: max streamed bytes.
        /// 端点级字节上限（0=用全局）。缓冲式=最多缓冲字节；流式=最多流过字节。
        /// </summary>
        public long MaxBytes { get; set; }
    }
}