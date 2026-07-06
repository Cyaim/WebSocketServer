namespace Cyaim.WebSocketServer.Infrastructure.StreamDispatch
{
    /// <summary>
    /// Wire format of a streaming-upload binary message, shared by all channels:
    /// [4-byte magic "\0WSU"][4-byte big-endian header length N][N-byte UTF8 JSON header][raw payload bytes...].
    /// The magic distinguishes a streaming upload from an ordinary binary message on the same connection
    /// (neither JSON nor a MessagePack request array starts with 0x00, so it cannot collide).
    /// 流式上传二进制消息的线格式（各通道共用）：魔数 + 大端头部长度 + JSON 头部 + 原始负载。
    /// 魔数用于把流式上传与同连接上的普通二进制消息区分开（JSON 与 MessagePack 请求数组都不以 0x00 开头）。
    /// </summary>
    public static class StreamUploadProtocol
    {
        /// <summary>The 4-byte magic prefix: 0x00 'W' 'S' 'U'.</summary>
        public static readonly byte[] Magic = { 0x00, (byte)'W', (byte)'S', (byte)'U' };

        /// <summary>Magic(4) + header-length prefix(4) that precede the JSON header. 魔数(4)+长度前缀(4)。</summary>
        public const int HeaderPrefixBytes = 8;

        /// <summary>True if the frame starts with the streaming-upload magic. 首帧是否以流式上传魔数开头。</summary>
        public static bool StartsWithMagic(byte[] buffer, int count)
        {
            return count >= 4
                && buffer[0] == Magic[0] && buffer[1] == Magic[1]
                && buffer[2] == Magic[2] && buffer[3] == Magic[3];
        }
    }
}
