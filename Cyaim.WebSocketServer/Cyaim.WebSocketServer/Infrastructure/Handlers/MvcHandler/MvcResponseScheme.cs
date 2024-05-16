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

    /// <summary>
    /// MvcResponseSchemeException
    /// </summary>
    public class MvcResponseSchemeException : Exception
    {
        /// <summary>
        /// MvcResponseSchemeException
        /// </summary>
        public MvcResponseSchemeException()
        {
        }

        /// <summary>
        /// MvcResponseSchemeException
        /// </summary>
        /// <param name="message"></param>
        public MvcResponseSchemeException(string message) : base(message)
        {
            Msg = message;
        }

        /// <summary>
        /// MvcResponseSchemeException
        /// </summary>
        /// <param name="message"></param>
        /// <param name="innerException"></param>
        public MvcResponseSchemeException(string message, Exception innerException) : base(message, innerException)
        {
            Msg = message;
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
