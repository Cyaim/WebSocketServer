using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    /// <summary>
    /// Web Socket client
    /// </summary>
    public struct WebSocketClient
    {
        /// <summary>
        /// Channel name
        /// </summary>
        public string Channel { get; set; }

        /// <summary>
        /// WebSocket
        /// </summary>
        public WebSocket WebSocket { get; set; }
    }
}
