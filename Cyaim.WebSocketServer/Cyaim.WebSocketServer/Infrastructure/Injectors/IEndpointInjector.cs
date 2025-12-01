using Microsoft.AspNetCore.Http;
using System.Net.WebSockets;

namespace Cyaim.WebSocketServer.Infrastructure.Injectors
{
    /// <summary>
    /// Endpoint 注入器接口，用于注入 HttpContext 和 WebSocket 到 endpoint 实例
    /// </summary>
    public interface IEndpointInjector
    {
        /// <summary>
        /// 注入 HttpContext 和 WebSocket 到 endpoint 实例
        /// </summary>
        /// <param name="instance">Endpoint 实例</param>
        /// <param name="httpContext">HTTP 上下文</param>
        /// <param name="webSocket">WebSocket 实例</param>
        void Inject(object instance, HttpContext httpContext, WebSocket webSocket);
    }
}

