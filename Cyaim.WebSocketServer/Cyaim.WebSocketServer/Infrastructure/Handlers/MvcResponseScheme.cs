using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    /// <summary>
    /// WebSocket communication response scheme
    /// </summary>
    public class MvcResponseScheme
    {
        /// <summary>
        /// Response status.
        /// Success:0,Application Error:1,NotFoundTarget:2
        /// </summary>
        public int Status { get; set; }

        /// <summary>
        /// Response message
        /// </summary>
        public string Msg { get; set; }

        /// <summary>
        /// Request time tick
        /// </summary>
        public long ReauestTime { get; set; }

        /// <summary>
        /// Handle complate time tick
        /// </summary>
        public long ComplateTime { get; set; }

        /// <summary>
        /// Response body
        /// </summary>
        public object Body { get; set; }
    }
}
