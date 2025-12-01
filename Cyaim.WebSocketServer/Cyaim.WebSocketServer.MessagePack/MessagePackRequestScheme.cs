using MessagePack;

namespace Cyaim.WebSocketServer.MessagePack
{
    /// <summary>
    /// WebSocket communication scheme using MessagePack
    /// </summary>
    [MessagePackObject]
    public class MessagePackRequestScheme
    {
        /// <summary>
        /// Request Id
        /// In Multiplex, you need to keep the id of uniqueness
        /// </summary>
        [Key(0)]
        public string Id { get; set; }

        /// <summary>
        /// Request target
        /// </summary>
        [Key(1)]
        public string Target { get; set; }

        /// <summary>
        /// Request context
        /// </summary>
        [Key(2)]
        public object Body { get; set; }
    }
}

