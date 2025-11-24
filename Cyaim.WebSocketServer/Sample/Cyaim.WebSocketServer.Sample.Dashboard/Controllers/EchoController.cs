using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Sample.Dashboard.Controllers
{
    /// <summary>
    /// Echo WebSocket controller example
    /// Echo WebSocket 控制器示例
    /// </summary>
    public class EchoController : IWebSocketSession
    {
        private readonly ILogger<EchoController> _logger;

        public EchoController(ILogger<EchoController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// WebSocket HTTP context / WebSocket HTTP 上下文
        /// </summary>
        public HttpContext? WebSocketHttpContext { get; set; }

        /// <summary>
        /// WebSocket client instance / WebSocket 客户端实例
        /// </summary>
        public WebSocket? WebSocketClient { get; set; }

        /// <summary>
        /// Echo message handler
        /// Echo 消息处理器
        /// </summary>
        /// <param name="message">Message to echo / 要回显的消息</param>
        [WebSocket("echo")]
        public async Task<string> Echo(string message)
        {
            if (WebSocketClient == null || WebSocketClient.State != WebSocketState.Open)
            {
                return "WebSocket is not connected";
            }

            var response = $"Echo: {message}";
            var bytes = Encoding.UTF8.GetBytes(response);
            await WebSocketClient.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                System.Threading.CancellationToken.None);
            return response;
        }

        /// <summary>
        /// Get server time
        /// 获取服务器时间
        /// </summary>
        [WebSocket("time")]
        public async Task<string> GetTime()
        {
            if (WebSocketClient == null || WebSocketClient.State != WebSocketState.Open)
            {
                return "WebSocket is not connected";
            }

            var response = $"Server time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            var bytes = Encoding.UTF8.GetBytes(response);
            await WebSocketClient.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                System.Threading.CancellationToken.None);
            return response;
        }
    }
}

