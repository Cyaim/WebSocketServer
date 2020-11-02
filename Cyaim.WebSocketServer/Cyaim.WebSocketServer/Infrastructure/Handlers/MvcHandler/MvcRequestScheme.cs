using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler
{
    /// <summary>
    /// WebSocket communication scheme
    /// </summary>
    public class MvcRequestScheme
    {
        /// <summary>
        /// Request Id
        /// In Multiplex, you need to keep the id of uniqueness
        /// </summary>
        public string Id { get; set; }

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
