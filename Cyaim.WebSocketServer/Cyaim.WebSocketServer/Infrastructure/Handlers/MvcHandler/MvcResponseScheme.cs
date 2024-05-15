using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler
{
    /// <summary>
    /// WebSocket communication response scheme
    /// </summary>
    public class MvcResponseScheme : IMvcScheme
    {
        /// <summary>
        /// Response Id with request consistent 
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Target when responding to return requests 
        /// </summary>
        public string Target { get; set; }

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
        public long RequestTime { get; set; }

        /// <summary>
        /// Handle complate time tick
        /// </summary>
        public long ComplateTime { get; set; }

        /// <summary>
        /// Response body
        /// </summary>
        public object Body { get; set; }

    }

    public class MvcResponseSchemeException : Exception
    {
        public MvcResponseSchemeException()
        {
        }

        public MvcResponseSchemeException(string message) : base(message)
        {
            Msg = message;
        }

        public MvcResponseSchemeException(string message, Exception innerException) : base(message, innerException)
        {
            Msg = message;
        }

        protected MvcResponseSchemeException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }


        /// <summary>
        /// Response Id with request consistent 
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Target when responding to return requests 
        /// </summary>
        public string Target { get; set; }

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
        public long RequestTime { get; set; }

        /// <summary>
        /// Handle complate time tick
        /// </summary>
        public long ComplateTime { get; set; }
    }
}
