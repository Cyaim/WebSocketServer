using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Example.Wpf.Controllers
{
    public class TestController
    {
        private readonly ILogger<TestController> _logger;

        public TestController(ILogger<TestController> logger)
        {
            _logger = logger;
        }

        public HttpContext? WebSocketHttpContext { get; set; }

        public WebSocket? WebSocketClient { get; set; }

        /// <summary>
        /// 测试
        /// </summary>
        /// <returns></returns>
        [WebSocket]
        public async Task<int> Test()
        {
            var test = "1234567890-~1231124124142141241414141412419898324792087";
            var bytes = Encoding.UTF8.GetBytes(test);
            await WebSocketClient!.SendAsync(bytes[..10], WebSocketMessageType.Text, false, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(1000).ConfigureAwait(false);
            await WebSocketClient!.SendAsync(bytes[10..18], WebSocketMessageType.Text, false, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);
            await WebSocketClient!.SendAsync(bytes[18..], WebSocketMessageType.Text, false, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(3000).ConfigureAwait(false);
            await WebSocketClient!.SendAsync(Memory<byte>.Empty, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

            return await Task.FromResult(0).ConfigureAwait(false);
        }
    }
}
