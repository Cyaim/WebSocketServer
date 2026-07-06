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

            long streamed = 0;
            bool overCap = false;
            Exception feedError = null;
            try
            {
                int dataOff = StreamUploadProtocol.HeaderPrefixBytes + headerLen;
                int dataLen = count - dataOff;
                if (dataLen > 0)
                {
                    streamed += dataLen;
                    await pipe.Writer.WriteAsync(buffer.AsMemory(dataOff, dataLen), cts.Token);
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
                        FlushResult fr = await pipe.Writer.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
                        if (fr.IsCompleted || fr.IsCanceled) { break; }
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
