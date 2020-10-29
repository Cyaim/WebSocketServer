using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Attributes
{
    /// <summary>
    /// WebSocket Endpoint mark
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class WebSocketAttribute : Attribute
    {
        /// <summary>
        /// Mark action use action name
        /// </summary>
        public WebSocketAttribute()
        {
        }

        /// <summary>
        /// Mark action use method value
        /// </summary>
        /// <param name="method"></param>
        public WebSocketAttribute(string method) : this()
        {
            Method = method;
        }

        /// <summary>
        /// Endpoint method name
        /// </summary>
        public string Method { get; set; }
    }
}
