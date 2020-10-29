using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    /// <summary>
    /// WebSocket communication scheme
    /// </summary>
    public class MvcRequestScheme
    {
        /// <summary>
        /// Request target
        /// </summary>
        public string Target { get; set; }

        /// <summary>
        /// Request context
        /// </summary>
        public object Body { get; set; }
    }
}
