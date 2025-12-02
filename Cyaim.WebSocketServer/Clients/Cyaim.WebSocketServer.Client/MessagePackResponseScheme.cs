using MessagePack;

namespace Cyaim.WebSocketServer.Client
{
    /// <summary>
    /// MessagePack response scheme / MessagePack 响应方案
    /// </summary>
    [MessagePackObject]
    internal class MessagePackResponseScheme
    {
        [Key(0)]
        public int Status { get; set; }

        [Key(1)]
        public string? Msg { get; set; }

        [Key(2)]
        public long RequestTime { get; set; }

        [Key(3)]
        public long CompleteTime { get; set; }

        [Key(4)]
        public string Id { get; set; } = string.Empty;

        [Key(5)]
        public string? Target { get; set; }

        [Key(6)]
        public object? Body { get; set; }
    }
}

