using System;
using MessagePack;

namespace Cyaim.WebSocketServer.MessagePack
{
    /// <summary>
    /// WebSocket communication response scheme using MessagePack
    /// </summary>
    [MessagePackObject]
    public class MessagePackResponseScheme
    {
        /// <summary>
        /// Response status.
        /// Success:0,Application Error:1,NotFoundTarget:2
        /// </summary>
        [Key(0)]
        public int Status { get; set; }

        /// <summary>
        /// Response message
        /// </summary>
        [Key(1)]
        public string Msg { get; set; }

        /// <summary>
        /// Request time tick
        /// </summary>
        [Key(2)]
        public long RequestTime { get; set; }

        /// <summary>
        /// Handle complete time tick
        /// </summary>
        [Key(3)]
        public long CompleteTime { get; set; }

        /// <summary>
        /// Response Id with request consistent
        /// </summary>
        [Key(4)]
        public string Id { get; set; }

        /// <summary>
        /// Target when responding to return requests
        /// </summary>
        [Key(5)]
        public string Target { get; set; }

        /// <summary>
        /// Response body
        /// </summary>
        [Key(6)]
        public object Body { get; set; }
    }

    /// <summary>
    /// MessagePackResponseSchemeException
    /// </summary>
    public class MessagePackResponseSchemeException : Exception
    {
        /// <summary>
        /// MessagePackResponseSchemeException
        /// </summary>
        public MessagePackResponseSchemeException() { }

        /// <summary>
        /// MessagePackResponseSchemeException
        /// </summary>
        /// <param name="message"></param>
        public MessagePackResponseSchemeException(string message) : base(message)
        {
            Msg = message;
        }

        /// <summary>
        /// MessagePackResponseSchemeException
        /// </summary>
        /// <param name="message"></param>
        /// <param name="innerException"></param>
        public MessagePackResponseSchemeException(string message, Exception innerException) : base(message, innerException)
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
        /// Handle complete time tick
        /// </summary>
        public long CompleteTime { get; set; }
    }
}

