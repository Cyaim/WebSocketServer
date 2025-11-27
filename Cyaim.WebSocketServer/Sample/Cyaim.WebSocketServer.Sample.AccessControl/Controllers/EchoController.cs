using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace Cyaim.WebSocketServer.Sample.AccessControl.Controllers
{
    /// <summary>
    /// Echo controller for testing WebSocket connections / 用于测试 WebSocket 连接的 Echo 控制器
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class EchoController : ControllerBase
    {
        /// <summary>
        /// Echo endpoint / Echo 端点
        /// </summary>
        /// <param name="message">Message to echo / 要回显的消息</param>
        /// <returns>Echoed message / 回显的消息</returns>
        [WebSocket]
        [HttpGet]
        public string Echo(string message)
        {
            return $"Echo: {message}";
        }
    }
}

