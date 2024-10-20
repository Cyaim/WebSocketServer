using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// WebSocket operation method
    /// </summary>
    public static class WebSocketManager
    {
        /// <summary>
        /// Upper limit of WebSockets processed per batch
        /// </summary>
        public static int BatchProcessingWebsocketLimit { get; set; } = 1000;

        /// <summary>
        /// Default send encoding
        /// </summary>
        private static Encoding DefaultEncoding { get; } = Encoding.UTF8;

        /// <summary>
        /// Channel for sending tasks
        /// </summary>
        private static Channel<SendItem> sendChannel = Channel.CreateUnbounded<SendItem>();

        class SendItem : IDisposable
        {
            public Stream Stream { get; set; }
            public ReadOnlyMemory<byte> Buffer { get; set; }


            /// <summary>
            /// true if the Send all data at once, false if the send in batches according to SendBufferSize size
            /// </summary>
            public bool SendAtOnce { get; set; }
            /// <summary>
            /// Client to be sent
            /// </summary>
            public List<KeyValuePair<WebSocket, (WebSocketMessageType MessgaeType, CancellationToken CancellationToken)>> Sockets { get; set; }

            /// <summary>
            /// Send buffer size, default 4K
            /// </summary>
            public uint SendBufferSize { get; set; } = 4 * 1024;

            /// <summary>
            /// Whether to send the completed flag and release it after sending is completed
            /// </summary>
            public SemaphoreSlim SendCompletedSemaphore { get; } = new SemaphoreSlim(0, 1);

            /// <summary>
            /// Internal error
            /// </summary>
            public Exception Exception { get; set; }

            public void Dispose()
            {
                try
                {
                    Stream?.Dispose();
                    Sockets.Clear();
                    Sockets = null;
                    SendCompletedSemaphore?.Dispose();
                }
                catch { }
            }
        }

        static WebSocketManager()
        {
            // Start listen channel
            _ = ListenSendChannel();
        }

        #region Send channel

        /// <summary>
        /// Listen send channel
        /// </summary>
        /// <returns></returns>
        private static async Task ListenSendChannel()
        {
            while (await sendChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                //lock (alarmCheck_locker)
                //{

                while (sendChannel.Reader.TryRead(out SendItem item))
                {
                    try
                    {
                        await SendItemInBatchesAsync(item);
                    }
                    catch (Exception ex)
                    {
                        item.Exception = ex;
                    }
                    finally
                    {
                        item.SendCompletedSemaphore.Release();
                    }

                    //ParallelLoopResult result = Parallel.ForEach(item.Sockets, async (s, state) =>
                    //{
                    //    try
                    //    {
                    //        if (item.CancellationToken.IsCancellationRequested)
                    //        {
                    //            state.Stop();
                    //            return;
                    //        }
                    //        if (s.Key.State == WebSocketState.Open)
                    //        {
                    //            await s.SendAsync(item.Buffer, item.MessageType, item.EndOfMessage, item.CancellationToken);
                    //        }
                    //    }
                    //    catch (AggregateException age)
                    //    {
                    //        foreach (var item in age.InnerExceptions)
                    //        {
                    //            Console.WriteLine(item.Message);
                    //        }
                    //    }
                    //});
                }
                //}
            }
        }

        /// <summary>
        /// Websocket senditem 
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        private static async Task SendItemInBatchesAsync(SendItem item)
        {
            // 分批发送数据
            for (int i = 0; i < item.Sockets.Count; i += BatchProcessingWebsocketLimit)
            {
                //var batch = item.Sockets.Skip(i).Take(BatchProcessingWebsocketLimit);
                var batch = item.Sockets.GetRange(i, Math.Min(BatchProcessingWebsocketLimit, item.Sockets.Count - i));
                var sendTasks1 = batch.Select(socketInfo =>
                {
                    // 检查取消发送的标记
                    if (socketInfo.Value.CancellationToken.IsCancellationRequested)
                    {
                        return Task.FromException(new OperationCanceledException(socketInfo.Value.CancellationToken));
                    }

                    try
                    {
                        // 发送数据
                        if (item.Stream != null && item.Stream.CanRead)
                        {
                            if (item.SendAtOnce)
                            {
                                return SendStreamDataAsync(socketInfo.Key, socketInfo.Value.MessgaeType, item.Stream, socketInfo.Value.CancellationToken);
                            }
                            else
                            {
                                return SendStreamDataInBatchesAsync(socketInfo.Key, socketInfo.Value.MessgaeType, item.Stream, item.SendBufferSize, socketInfo.Value.CancellationToken);
                            }
                        }
                        else if (item.Buffer.Length > 0)
                        {
                            if (item.SendAtOnce)
                            {
                                return socketInfo.Key.SendAsync(item.Buffer, socketInfo.Value.MessgaeType, endOfMessage: true, socketInfo.Value.CancellationToken).AsTask();
                            }
                            else
                            {
                                return SendBufferedDataInBatchesAsync(socketInfo.Key, socketInfo.Value.MessgaeType, item.Buffer, item.SendBufferSize, socketInfo.Value.CancellationToken);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        return Task.FromException(ex);
                    }
                    return Task.CompletedTask;
                }).ToList();
                try
                {

                    await Task.WhenAll(sendTasks1);
                }
                catch (Exception)
                { }
            }
        }

        /// <summary>
        /// Websocket senditem 
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        private static async Task SendItemAsync(SendItem item)
        {
            List<Task> sendTasks = item.Sockets
                .Where(s => s.Key.State == WebSocketState.Open)
                .Select(socketInfo =>
                {
                    // 检查取消发送的标记
                    if (socketInfo.Value.CancellationToken.IsCancellationRequested)
                    {
                        return Task.FromException(new OperationCanceledException(socketInfo.Value.CancellationToken));
                    }

                    try
                    {
                        // 发送数据
                        if (item.Stream != null && item.Stream.CanRead)
                        {
                            if (item.SendAtOnce)
                            {
                                return SendStreamDataAsync(socketInfo.Key, socketInfo.Value.MessgaeType, item.Stream, socketInfo.Value.CancellationToken);
                            }
                            else
                            {
                                return SendStreamDataInBatchesAsync(socketInfo.Key, socketInfo.Value.MessgaeType, item.Stream, item.SendBufferSize, socketInfo.Value.CancellationToken);
                            }
                        }
                        else if (item.Buffer.Length > 0)
                        {
                            if (item.SendAtOnce)
                            {
                                return socketInfo.Key.SendAsync(item.Buffer, socketInfo.Value.MessgaeType, endOfMessage: true, socketInfo.Value.CancellationToken).AsTask();
                            }
                            else
                            {
                                return SendBufferedDataInBatchesAsync(socketInfo.Key, socketInfo.Value.MessgaeType, item.Buffer, item.SendBufferSize, socketInfo.Value.CancellationToken);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        return Task.FromException(ex);
                    }
                    return Task.CompletedTask;
                }).ToList();
            await Task.WhenAll(sendTasks);
        }

        /// <summary>
        /// Send data from the stream (all at once)
        /// 从流中发送数据（一次性发送）
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="messageType"></param>
        /// <param name="stream"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task SendStreamDataAsync(WebSocket webSocket, WebSocketMessageType messageType, Stream stream, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent((int)stream.Length);
            try
            {
                await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                await webSocket.SendAsync(buffer, messageType, endOfMessage: true, cancellationToken);
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Send data in batches from the stream
        /// 从流中分批发送数据
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="messageType"></param>
        /// <param name="stream"></param>
        /// <param name="bufferSize"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task SendStreamDataInBatchesAsync(WebSocket webSocket, WebSocketMessageType messageType, Stream stream, uint bufferSize, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent((int)bufferSize);
            try
            {
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await webSocket.SendAsync(buffer.AsMemory(0, bytesRead), messageType, endOfMessage: false, cancellationToken);
                }

                await webSocket.SendAsync(Memory<byte>.Empty, messageType, endOfMessage: true, cancellationToken);
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Send data in batches from the buffer
        /// 从缓冲区中分批发送数据
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="messageType"></param>
        /// <param name="buffer"></param>
        /// <param name="batchSize"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task SendBufferedDataInBatchesAsync(WebSocket webSocket, WebSocketMessageType messageType, ReadOnlyMemory<byte> buffer, uint batchSize, CancellationToken cancellationToken)
        {
            try
            {
                int offset = 0;

                while (offset < buffer.Length)
                {
                    int count = Math.Min((int)batchSize, buffer.Length - offset);
                    await webSocket.SendAsync(buffer.Slice(offset, count), messageType, endOfMessage: false, CancellationToken.None);
                    offset += count;
                }

                await webSocket.SendAsync(Memory<byte>.Empty, messageType, endOfMessage: true, cancellationToken);
            }
            catch (Exception)
            {

                throw;
            }
        }
        #endregion

        /// <summary>
        /// Send data without buffer
        /// </summary>
        /// <param name="sendStream"></param>
        /// <param name="messageType"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="sendBufferSize"></param>
        /// <param name="sockets"></param>
        /// <returns></returns>
        public static async Task SendAsync(Stream sendStream, WebSocketMessageType messageType, CancellationToken cancellationToken, TimeSpan? timeout = null, bool sendAtOnce = false, uint sendBufferSize = 4 * 1024, params WebSocket[] sockets)
        {
            if (sockets == null || sockets.LongLength < 1 || sendBufferSize < 1)
            {
                return;
            }
            using var sendItem = new SendItem
            {
                Stream = sendStream,
                SendAtOnce = sendAtOnce,
                Sockets = sockets.Where(x => x != null && x.State == WebSocketState.Open).Select(x => KeyValuePair.Create(x, (messageType, cancellationToken))).ToList(),
                SendBufferSize = sendBufferSize,
            };
            await sendChannel.Writer.WriteAsync(sendItem, cancellationToken).ConfigureAwait(false);


            await sendItem.SendCompletedSemaphore.WaitAsync(timeout ?? Timeout.InfiniteTimeSpan, cancellationToken);
        }

        /// <summary>
        /// Send data without buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="messageType"></param>
        /// <param name="sendAtOnce"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="sendBufferSize"></param>
        /// <param name="sockets"></param>
        /// <returns></returns>
        public static async Task SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool sendAtOnce, CancellationToken cancellationToken, TimeSpan? timeout = null, uint sendBufferSize = 4 * 1024, params WebSocket[] sockets)
        {
            if (sockets == null || sockets.LongLength < 1)
            {
                return;
            }
            using var sendItem = new SendItem
            {
                Buffer = buffer,
                SendAtOnce = sendAtOnce,
                Sockets = sockets.Where(x => x != null && x.State == WebSocketState.Open).Select(x => KeyValuePair.Create(x, (messageType, cancellationToken))).ToList(),
                SendBufferSize = sendBufferSize,
            };
            if (sendItem.Sockets.Count < 1)
                throw new ArgumentNullException(nameof(sendItem.Sockets));

            await sendChannel.Writer.WriteAsync(sendItem, cancellationToken).ConfigureAwait(false);

            await sendItem.SendCompletedSemaphore.WaitAsync(timeout ?? Timeout.InfiniteTimeSpan, cancellationToken);

            //ParallelLoopResult result = Parallel.ForEach(sockets, async (s, state) =>
            //{
            //    try
            //    {
            //        if (cancellationToken.IsCancellationRequested)
            //        {
            //            state.Stop();
            //            return;
            //        }
            //        if (s.State == WebSocketState.Open)
            //        {
            //            await s.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
            //        }
            //    }
            //    catch (AggregateException age)
            //    {
            //        foreach (var item in age.InnerExceptions)
            //        {
            //            Console.WriteLine(item.Message);
            //        }
            //    }
            //});
            //while (!result.IsCompleted)
            //{
            //    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            //}
        }

        /// <summary>
        /// Send data without buffer
        /// </summary>
        /// <param name="data"></param>
        /// <param name="messageType"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="timeout"></param>
        /// <param name="encoding"></param>
        /// <param name="sendBufferSize"></param>
        /// <param name="socket"></param>
        /// <returns></returns>
        public static async Task SendAsync(
            string data,
            WebSocketMessageType messageType,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null,
            Encoding encoding = null,
            int sendBufferSize = 4 * 1024,
            params WebSocket[] socket)
        {
            if (string.IsNullOrEmpty(data) || socket == null || socket.LongLength < 1)
            {
                return;
            }
            encoding ??= DefaultEncoding;
            var sendData = encoding.GetBytes(data);
            await SendAsync(sendData, messageType, sendData.Length <= 4 * 1024, cancellationToken: cancellationToken, timeout, sendBufferSize: (uint)sendBufferSize, sockets: socket);
        }

        /// <summary>
        /// Sending serialized model text data without using a buffer
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="options"></param>
        /// <param name="messageType"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="timeout"></param>
        /// <param name="sendBufferSize"></param>
        /// <param name="socket"></param>
        /// <returns></returns>
        public static async Task SendAsync<T>(
            this T data,
            JsonSerializerOptions options = null,
            WebSocketMessageType messageType = WebSocketMessageType.Text,
            CancellationToken? cancellationToken = null,
            TimeSpan? timeout = null,
            Encoding encoding = null,
            int sendBufferSize = 4 * 1024,
            params WebSocket[] socket)
        {
            if (data == null || socket == null || socket.LongLength < 1)
            {
                return;
            }
            await SendAsync(JsonSerializer.Serialize(data, options), messageType, cancellationToken ?? CancellationToken.None, timeout, encoding, sendBufferSize, socket);
        }

        /// <summary>
        /// Sending serialized model text data without using a buffer
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="socket"></param>
        /// <param name="data"></param>
        /// <param name="options"></param>
        /// <param name="messageType"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="timeout"></param>
        /// <param name="encoding"></param>
        /// <param name="sendBufferSize"></param>
        /// <returns></returns>
        public static async Task SendAsync<T>(
            this WebSocket socket,
            T data,
            JsonSerializerOptions options = null,
            WebSocketMessageType messageType = WebSocketMessageType.Text,
            CancellationToken? cancellationToken = null,
            TimeSpan? timeout = null,
            Encoding encoding = null,
            int sendBufferSize = 4 * 1024)
        {
            if (data == null || socket == null)
            {
                return;
            }
            await SendAsync(JsonSerializer.Serialize(data, options), messageType, cancellationToken ?? CancellationToken.None, timeout, encoding, sendBufferSize, socket);
        }
    }



}