using System;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// 载荷超过发送侧物化上限，且不允许降级流式时抛出。**一帧都没有发出，连接不受影响。**
    /// Thrown when a payload exceeds the send-side materialization limit and the chunked fallback is not
    /// available. <b>No frame was written and the connection is unaffected.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单独立一个异常类型，是因为调用方需要把「太大所以没发」与「socket 已关」「IO 错误」区分开：
    /// 前者重试无用、要么调高上限要么在应用层分块，后者才是重试或重连的场景。
    /// 库里几处按连接 ID 发送的 API 返回 <c>bool</c>，这个类型让它们在裸 catch 里穿透，
    /// 而不是被压成一个无从解释的 <c>false</c>。
    /// This gets its own type because callers must tell "too large, so nothing was sent" apart from
    /// "the socket is gone" and "an IO error occurred": retrying helps the latter, never the former,
    /// which needs either a higher limit or application-level chunking. Several send-by-connection-id
    /// APIs return <c>bool</c>; this type is allowed through their catch blocks rather than being
    /// flattened into an unexplainable <c>false</c>.
    /// </para>
    /// <para>
    /// 继承 <see cref="InvalidOperationException"/>：这是调用方用法问题（交了一个对当前配置而言过大的
    /// 载荷），不是传输故障。
    /// Derives from <see cref="InvalidOperationException"/>: this is a usage problem (handing over a
    /// payload too large for the current configuration), not a transport fault.
    /// </para>
    /// </remarks>
    public class WebSocketMessageTooLargeException : InvalidOperationException
    {
        public WebSocketMessageTooLargeException(long? payloadBytes, long limitBytes, string message)
            : base(message)
        {
            PayloadBytes = payloadBytes;
            LimitBytes = limitBytes;
        }

        /// <summary>
        /// 载荷字节数；源流不报告长度时为 null。
        /// Payload size in bytes, or null when the source stream does not report a length.
        /// </summary>
        public long? PayloadBytes { get; }

        /// <summary>生效的物化上限。The materialization limit that was in force.</summary>
        public long LimitBytes { get; }
    }
}
