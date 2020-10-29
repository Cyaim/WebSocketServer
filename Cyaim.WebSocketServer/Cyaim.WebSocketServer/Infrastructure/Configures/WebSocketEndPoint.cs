using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

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

    }
}
