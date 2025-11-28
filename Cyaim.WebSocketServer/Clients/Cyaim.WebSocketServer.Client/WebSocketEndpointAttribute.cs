using System;

namespace Cyaim.WebSocketServer.Client
{
    /// <summary>
    /// Attribute to specify the WebSocket endpoint target for a method
    /// 用于指定方法的 WebSocket 端点目标的特性
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class WebSocketEndpointAttribute : Attribute
    {
        /// <summary>
        /// Target endpoint path (e.g., "controller.action" or "weatherforecast.get")
        /// 目标端点路径（例如："controller.action" 或 "weatherforecast.get"）
        /// </summary>
        public string Target { get; set; }

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="target">Target endpoint path / 目标端点路径</param>
        public WebSocketEndpointAttribute(string target)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
        }
    }
}

