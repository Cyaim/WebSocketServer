using MessagePack;

namespace Cyaim.WebSocketServer.Client
{
    /// <summary>
    /// MessagePack request scheme / MessagePack 请求方案
    /// </summary>
    [MessagePackObject]
    public class MessagePackRequestScheme
    {
        [Key(0)]
        public string Id { get; set; } = string.Empty;

        [Key(1)]
        public string Target { get; set; } = string.Empty;

        [Key(2)]
        public object? Body { get; set; }
    }
}

