using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure.Configures;

namespace Cyaim.WebSocketServer.Infrastructure.StreamDispatch
{
    /// <summary>
    /// Outcome of receiving a streaming upload. When <see cref="Handled"/> is true the caller should serialize
    /// and send a response built from <see cref="Id"/>/<see cref="Target"/>/<see cref="Result"/> in its own
    /// wire format; when false the receiver already dealt with the message (drained a malformed/misrouted one,
    /// or sent 1009 + aborted an over-cap one) and the caller should do nothing.
    /// </summary>
    public readonly struct StreamForwardOutcome
    {
        public bool Handled { get; }
        public string Id { get; }
        public string Target { get; }
        public StreamInvokeResult Result { get; }
        public StreamForwardOutcome(bool handled, string id, string target, StreamInvokeResult result)
        {
            Handled = handled;
            Id = id;
            Target = target;
            Result = result;
        }
        public static StreamForwardOutcome NotHandled => new StreamForwardOutcome(false, null, null, default);
    }

    /// <summary>
    /// Channel-agnostic receiver for a streaming upload: parse the header, set up a Pipe, invoke the endpoint,
    /// and feed frames to it (constant memory — the payload is never fully buffered), enforcing the per-endpoint
    /// byte cap. Only the response encoding differs per channel, so that stays with the caller. The first frame
    /// must already have been identified as a streaming upload via <see cref="StreamUploadProtocol.StartsWithMagic"/>.
    /// </summary>
    public static class WebSocketStreamReceiver
    {
        public static async Task<StreamForwardOutcome> ReceiveAndInvokeAsync(
            WebSocket webSocket, HttpContext context, byte[] buffer, WebSocketReceiveResult firstResult,
            WebSocketRouteOption options, ILogger logger, CancellationToken connectionToken)
        {
            int count = firstResult.Count;
            // The header (magic + length prefix + JSON) must fit in the first frame.
            if (count < StreamUploadProtocol.HeaderPrefixBytes)
            {
                await WebSocketReceiveMemoryGovernor.DrainOversizedAsync(webSocket, buffer, firstResult);
                return StreamForwardOutcome.NotHandled;
            }
            int headerLen = (buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7];
            if (headerLen <= 0 || (long)StreamUploadProtocol.HeaderPrefixBytes + headerLen > count)
            {
                logger?.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, "Streaming upload header too large or split across frames."));
                await WebSocketReceiveMemoryGovernor.DrainOversizedAsync(webSocket, buffer, firstResult);
                return StreamForwardOutcome.NotHandled;
            }

            string target = null, id = null;
            JsonNode meta = null;
            try
            {
                var headerObj = JsonNode.Parse(buffer.AsSpan(StreamUploadProtocol.HeaderPrefixBytes, headerLen).ToArray()) as JsonObject;
                target = headerObj? ["target"]?.GetValue<string>();
                id = headerObj? ["id"]?.GetValue<string>();
                if (headerObj != null && headerObj.TryGetPropertyValue("meta", out var m)) { meta = m; }
            }
            catch
            {
                await WebSocketReceiveMemoryGovernor.DrainOversizedAsync(webSocket, buffer, firstResult);
                return StreamForwardOutcome.NotHandled;
            }

            if (target == null || options.WatchAssemblyContext == null
                || !options.WatchAssemblyContext.TryGetEndpointPolicy(target, out var pol) || !pol.IsStream)
            {
                logger?.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, "Binary message targeted a non-streaming endpoint."));
                await WebSocketReceiveMemoryGovernor.DrainOversizedAsync(webSocket, buffer, firstResult);
                return StreamForwardOutcome.NotHandled;
            }
            long maxBytes = pol.MaxBytes;

            var pipe = new Pipe();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
            Stream body = pipe.Reader.AsStream();
            Task<StreamInvokeResult> invokeTask = WebSocketStreamInvoker.InvokeAsync(options, context, webSocket, target, meta, body, cts.Token, logger);

            // The endpoint may return before it has read the body — validating a header and bailing out
            // is the ordinary way to reject an upload. Nobody reads the pipe after that, so the feed
            // loop below fills to PauseWriterThreshold (64 KiB by default) and its WriteAsync never
            // returns: CompleteAsync is never reached, `await invokeTask` is never reached, and
            // MvcStreamForward never returns — the connection's whole receive loop is wedged.
            //
            // It cannot be broken from outside either. webSocket.Abort() does not help, because the
            // block is on the pipe and not on the socket, and the linked token only fires on
            // application shutdown. And it needs no attacker: a legitimate large upload that hits a
            // validation failure gets the same result.
            //
            // Completing the reader when the endpoint finishes is what makes the write side always
            // resolvable — WriteAsync then returns IsCompleted and the loop breaks.
            //
            // 端点完全可能在读完 body 之前就返回——校验个头部就拒掉，本来就是拒绝上传的常规写法。
            // 此后没有人再读这条 pipe，于是下面的喂数据循环写到 PauseWriterThreshold（默认 64 KiB）
            // 就再也回不来：CompleteAsync 到不了、await invokeTask 到不了、MvcStreamForward 不返回，
            // **整条连接的接收循环就此焊死**。
            // 而且从外面解不开：Abort() 没用，因为阻塞在 pipe 上而不是 socket 上，
            // 那个链接令牌也只在应用关闭时才触发。它更不需要攻击者：
            // 一次正常的大文件上传撞上校验失败，结果一模一样。
            // 端点一结束就 Complete 掉 Reader，写入侧才**总是**解得开——WriteAsync 随即返回 IsCompleted，循环 break。
            _ = invokeTask.ContinueWith(
                static (_, state) =>
                {
                    try { ((Pipe)state).Reader.Complete(); }
                    catch { /* already completed, or the pipe is gone; either way the write side is free */ }
                },
                pipe,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            long streamed = 0;
            bool overCap = false;

            // Set when the endpoint has stopped reading. The frames keep being drained after that; see
            // the comment in the loop for why stopping instead would desync the connection.
            // 端点不再读之后置位。此后仍然继续把帧排空——为什么不能就此停下，见循环里那段注释。
            bool readerDone = false;
            Exception feedError = null;
            try
            {
                int dataOff = StreamUploadProtocol.HeaderPrefixBytes + headerLen;
                int dataLen = count - dataOff;
                if (dataLen > 0)
                {
                    streamed += dataLen;

                    // The FlushResult matters here for the same reason it does in the loop below: an
                    // endpoint that returned before reading anything completes the reader, and this
                    // first write is where a small upload would discover that.
                    // 这里也要看 FlushResult，理由和下面循环里那次一样：
                    // 一个还没开始读就返回的端点会把 reader 完成掉，而小上传正是在这第一次写入上撞见它。
                    FlushResult firstFlush = await pipe.Writer.WriteAsync(buffer.AsMemory(dataOff, dataLen), cts.Token);
                    readerDone = firstFlush.IsCompleted || firstFlush.IsCanceled;
                }
                var result = firstResult;
                while (!(result.EndOfMessage || result.CloseStatus.HasValue))
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.CloseStatus.HasValue) { break; }
                    if (result.Count > 0)
                    {
                        streamed += result.Count;
                        if (maxBytes > 0 && streamed > maxBytes)
                        {
                            overCap = true;
                            feedError = new InvalidOperationException("Upload exceeds the endpoint MaxBytes cap.");
                            break;
                        }
                        // Once the reader is gone the frames still have to be read off the socket —
                        // they just stop being written anywhere.
                        //
                        // Breaking out here instead would leave the rest of this message unread, and
                        // the outer receive loop would then take those frames for the start of the
                        // next one: a protocol desync, which is how this shows up in practice — the
                        // connection dies a message later, somewhere that looks unrelated. The break
                        // was already written that way; it was simply unreachable while nothing ever
                        // completed the reader.
                        //
                        // reader 没了之后，剩下的帧仍然必须从 socket 上读掉——只是不再写去任何地方。
                        // 在这里 break 会把这条消息的剩余部分留在 socket 里，
                        // 而外层接收循环会把那些帧当成**下一条消息的开头**：协议错位。
                        // 它在现实里的样子是「连接在一条消息之后死掉，死在一个看起来毫不相干的地方」。
                        // 这个 break 本来就是这么写的，只是在没有任何东西 Complete reader 之前它不可达。
                        if (!readerDone)
                        {
                            FlushResult fr = await pipe.Writer.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
                            readerDone = fr.IsCompleted || fr.IsCanceled;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                feedError = ex;
            }
            finally
            {
                await pipe.Writer.CompleteAsync(feedError);
            }

            StreamInvokeResult result2;
            try { result2 = await invokeTask; }
            catch (Exception ex) { result2 = new StreamInvokeResult(1, null, ex.Message); }

            if (overCap)
            {
                logger?.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_RequestSizeMaximumLimit));
                try { if (webSocket.State == WebSocketState.Open) { await webSocket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Upload exceeds size limit", CancellationToken.None); } } catch { }
                webSocket.Abort();
                return StreamForwardOutcome.NotHandled;
            }
            return new StreamForwardOutcome(true, id, target, result2);
        }
    }
}
