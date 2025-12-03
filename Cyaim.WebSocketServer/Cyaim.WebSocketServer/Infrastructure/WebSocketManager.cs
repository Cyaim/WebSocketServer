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
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

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
                int totalBytesRead = 0;
                int bytesRead;
                while (totalBytesRead < buffer.Length && (bytesRead = await stream.ReadAsync(buffer, totalBytesRead, buffer.Length - totalBytesRead, cancellationToken)) > 0)
                {
                    totalBytesRead += bytesRead;
                }

                if (totalBytesRead > 0)
                {
                    await webSocket.SendAsync(new ArraySegment<byte>(buffer, 0, totalBytesRead), messageType, endOfMessage: true, cancellationToken);
                }
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
        /// Send data to local WebSocket connections (single machine mode only)
        /// 向本地 WebSocket 连接发送数据（仅单机模式）
        /// </summary>
        /// <param name="sendStream">Stream to send / 要发送的流</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <param name="timeout">Timeout / 超时时间</param>
        /// <param name="sendAtOnce">Send at once / 是否一次性发送</param>
        /// <param name="sendBufferSize">Send buffer size / 发送缓冲区大小</param>
        /// <param name="sockets">Local WebSocket connections / 本地 WebSocket 连接</param>
        /// <returns></returns>
        public static async Task SendLocalAsync(Stream sendStream, WebSocketMessageType messageType, CancellationToken cancellationToken, TimeSpan? timeout = null, bool sendAtOnce = false, uint sendBufferSize = 4 * 1024, params WebSocket[] sockets)
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
        /// Send data to local WebSocket connections (single machine mode only)
        /// 向本地 WebSocket 连接发送数据（仅单机模式）
        /// </summary>
        /// <param name="buffer">Data buffer / 数据缓冲区</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="sendAtOnce">Send at once / 是否一次性发送</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <param name="timeout">Timeout / 超时时间</param>
        /// <param name="sendBufferSize">Send buffer size / 发送缓冲区大小</param>
        /// <param name="sockets">Local WebSocket connections / 本地 WebSocket 连接</param>
        /// <returns></returns>
        public static async Task SendLocalAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool sendAtOnce, CancellationToken cancellationToken, TimeSpan? timeout = null, uint sendBufferSize = 4 * 1024, params WebSocket[] sockets)
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
        /// Send text data to local WebSocket connections (single machine mode only)
        /// 向本地 WebSocket 连接发送文本数据（仅单机模式）
        /// </summary>
        /// <param name="data">Text data / 文本数据</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <param name="timeout">Timeout / 超时时间</param>
        /// <param name="encoding">Text encoding / 文本编码</param>
        /// <param name="sendBufferSize">Send buffer size / 发送缓冲区大小</param>
        /// <param name="socket">Local WebSocket connections / 本地 WebSocket 连接</param>
        /// <returns></returns>
        public static async Task SendLocalAsync(
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
            await SendLocalAsync(sendData, messageType, sendData.Length <= 4 * 1024, cancellationToken: cancellationToken, timeout, sendBufferSize: (uint)sendBufferSize, sockets: socket);
        }

        /// <summary>
        /// Send serialized JSON object to local WebSocket connections (single machine mode only)
        /// 向本地 WebSocket 连接发送序列化的 JSON 对象（仅单机模式）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <param name="timeout">Timeout / 超时时间</param>
        /// <param name="encoding">Text encoding / 文本编码</param>
        /// <param name="sendBufferSize">Send buffer size / 发送缓冲区大小</param>
        /// <param name="socket">Local WebSocket connections / 本地 WebSocket 连接</param>
        /// <returns></returns>
        public static async Task SendLocalAsync<T>(
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
            await SendLocalAsync(JsonSerializer.Serialize(data, options), messageType, cancellationToken ?? CancellationToken.None, timeout, encoding, sendBufferSize, socket);
        }

        /// <summary>
        /// Send serialized JSON object to local WebSocket connection (single machine mode only)
        /// 向本地 WebSocket 连接发送序列化的 JSON 对象（仅单机模式）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="socket">Local WebSocket connection / 本地 WebSocket 连接</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <param name="timeout">Timeout / 超时时间</param>
        /// <param name="encoding">Text encoding / 文本编码</param>
        /// <param name="sendBufferSize">Send buffer size / 发送缓冲区大小</param>
        /// <returns></returns>
        public static async Task SendLocalAsync<T>(
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
            await SendLocalAsync(JsonSerializer.Serialize(data, options), messageType, cancellationToken ?? CancellationToken.None, timeout, encoding, sendBufferSize, socket);
        }

        #region Unified Send Methods (Single Machine & Cluster) / 统一发送方法（单机和集群）

        /// <summary>
        /// Send message to connection(s) - automatically handles single machine or cluster mode
        /// 向连接发送消息 - 自动处理单机或集群模式
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Message data as byte array / 消息数据（字节数组）</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        /// <remarks>
        /// This method automatically detects if cluster is enabled:
        /// - If cluster is enabled: uses ClusterManager to route message (supports cross-node)
        /// - If cluster is disabled: sends directly to local WebSocket connection
        /// 此方法自动检测是否启用集群：
        /// - 如果启用集群：使用 ClusterManager 路由消息（支持跨节点）
        /// - 如果未启用集群：直接发送到本地 WebSocket 连接
        /// </remarks>
        public static async Task<bool> SendAsync(
            string connectionId,
            byte[] data,
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            if (string.IsNullOrEmpty(connectionId) || data == null || data.Length == 0)
            {
                return false;
            }

            // Call batch method for single connection / 调用批量方法处理单个连接
            var results = await SendAsync(new[] { connectionId }, data, messageType);
            return results.TryGetValue(connectionId, out var success) && success;
        }

        /// <summary>
        /// Send text message to connection(s) - automatically handles single machine or cluster mode
        /// 向连接发送文本消息 - 自动处理单机或集群模式
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="text">Text message / 文本消息</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendAsync(
            string connectionId,
            string text,
            Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            encoding ??= DefaultEncoding;
            var data = encoding.GetBytes(text);
            // Call batch method for single connection / 调用批量方法处理单个连接
            var results = await SendAsync(new[] { connectionId }, data, WebSocketMessageType.Text);
            return results.TryGetValue(connectionId, out var success) && success;
        }

        /// <summary>
        /// Send JSON object to connection(s) - automatically handles single machine or cluster mode
        /// 向连接发送 JSON 对象 - 自动处理单机或集群模式
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendAsync<T>(
            string connectionId,
            T data,
            JsonSerializerOptions options = null,
            Encoding encoding = null)
        {
            if (data == null)
            {
                return false;
            }

            encoding ??= DefaultEncoding;
            var json = JsonSerializer.Serialize(data, options);
            var bytes = encoding.GetBytes(json);
            // Call batch method for single connection / 调用批量方法处理单个连接
            var results = await SendAsync(new[] { connectionId }, bytes, WebSocketMessageType.Text);
            return results.TryGetValue(connectionId, out var success) && success;
        }

        /// <summary>
        /// Send message to connection(s) - automatically handles single machine or cluster mode (supports batch)
        /// 向连接发送消息 - 自动处理单机或集群模式（支持批量）
        /// </summary>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表（支持单个或多个）</param>
        /// <param name="data">Message data as byte array / 消息数据（字节数组）</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        /// <remarks>
        /// This method automatically detects if cluster is enabled:
        /// - If cluster is enabled: uses ClusterManager to route message (supports cross-node)
        /// - If cluster is disabled: sends directly to local WebSocket connection
        /// 此方法自动检测是否启用集群：
        /// - 如果启用集群：使用 ClusterManager 路由消息（支持跨节点）
        /// - 如果未启用集群：直接发送到本地 WebSocket 连接
        /// </remarks>
        public static async Task<Dictionary<string, bool>> SendAsync(
            IEnumerable<string> connectionIds,
            byte[] data,
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            if (connectionIds == null)
            {
                return new Dictionary<string, bool>();
            }

            var results = new Dictionary<string, bool>();

            // Check if cluster is enabled / 检查是否启用集群
            var clusterManager = GlobalClusterCenter.ClusterManager;
            if (clusterManager != null)
            {
                // Use cluster routing / 使用集群路由
                return await clusterManager.RouteMessagesAsync(connectionIds, data, (int)messageType);
            }
            else
            {
                // Use local WebSocket / 使用本地 WebSocket
                var tasks = new List<Task>();
                foreach (var connectionId in connectionIds)
                {
                    if (string.IsNullOrEmpty(connectionId))
                    {
                        continue;
                    }

                    var task = Task.Run(async () =>
                    {
                        var webSocket = GetLocalWebSocket(connectionId);
                        if (webSocket != null && webSocket.State == WebSocketState.Open)
                        {
                            try
                            {
                                await webSocket.SendAsync(
                                    new ArraySegment<byte>(data),
                                    messageType,
                                    true,
                                    CancellationToken.None);
                                results[connectionId] = true;
                            }
                            catch
                            {
                                results[connectionId] = false;
                            }
                        }
                        else
                        {
                            results[connectionId] = false;
                        }
                    });
                    tasks.Add(task);
                }

                await Task.WhenAll(tasks);
                return results;
            }
        }

        /// <summary>
        /// Send text message to connection(s) - automatically handles single machine or cluster mode (supports batch)
        /// 向连接发送文本消息 - 自动处理单机或集群模式（支持批量）
        /// </summary>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表（支持单个或多个）</param>
        /// <param name="text">Text message / 文本消息</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendAsync(
            IEnumerable<string> connectionIds,
            string text,
            Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new Dictionary<string, bool>();
            }

            encoding ??= DefaultEncoding;
            var data = encoding.GetBytes(text);
            return await SendAsync(connectionIds, data, WebSocketMessageType.Text);
        }

        /// <summary>
        /// Send JSON object to connection(s) - automatically handles single machine or cluster mode (supports batch)
        /// 向连接发送 JSON 对象 - 自动处理单机或集群模式（支持批量）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表（支持单个或多个）</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendAsync<T>(
            IEnumerable<string> connectionIds,
            T data,
            JsonSerializerOptions options = null,
            Encoding encoding = null)
        {
            if (data == null)
            {
                return new Dictionary<string, bool>();
            }

            encoding ??= DefaultEncoding;
            var json = JsonSerializer.Serialize(data, options);
            var bytes = encoding.GetBytes(json);
            return await SendAsync(connectionIds, bytes, WebSocketMessageType.Text);
        }

        /// <summary>
        /// Get local WebSocket connection by connection ID / 根据连接 ID 获取本地 WebSocket 连接
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <returns>WebSocket instance or null / WebSocket 实例或 null</returns>
        private static WebSocket GetLocalWebSocket(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
            {
                return null;
            }

            // Try to get from MvcChannelHandler / 尝试从 MvcChannelHandler 获取
            if (MvcChannelHandler.Clients != null && MvcChannelHandler.Clients.TryGetValue(connectionId, out var webSocket))
            {
                return webSocket;
            }

            // Try to get from GlobalClusterCenter connection provider / 尝试从 GlobalClusterCenter 连接提供者获取
            var connectionProvider = GlobalClusterCenter.ConnectionProvider;
            if (connectionProvider != null)
            {
                return connectionProvider.GetConnection(connectionId);
            }

            return null;
        }

        #endregion

        #region Extension Methods for Convenient Usage / 扩展方法（便于使用）

        /// <summary>
        /// Send byte array to connection(s) - extension method for convenient usage
        /// 向连接发送字节数组 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="data">Message data as byte array / 消息数据（字节数组）</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendAsync(
            this byte[] data,
            string connectionId,
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            return await SendAsync(connectionId, data, messageType);
        }

        /// <summary>
        /// Send byte array to multiple connections - extension method for convenient usage
        /// 向多个连接发送字节数组 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="data">Message data as byte array / 消息数据（字节数组）</param>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendAsync(
            this byte[] data,
            IEnumerable<string> connectionIds,
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            return await SendAsync(connectionIds, data, messageType);
        }

        /// <summary>
        /// Send text message to connection(s) - extension method for convenient usage
        /// 向连接发送文本消息 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="text">Text message / 文本消息</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendTextAsync(
            this string text,
            string connectionId,
            Encoding encoding = null)
        {
            return await SendAsync(connectionId, text, encoding);
        }

        /// <summary>
        /// Send text message to multiple connections - extension method for convenient usage
        /// 向多个连接发送文本消息 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="text">Text message / 文本消息</param>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendTextAsync(
            this string text,
            IEnumerable<string> connectionIds,
            Encoding encoding = null)
        {
            return await SendAsync(connectionIds, text, encoding);
        }

        /// <summary>
        /// Send JSON object to connection(s) - extension method for convenient usage (excludes string type)
        /// 向连接发送 JSON 对象 - 扩展方法（便于使用，排除 string 类型）
        /// </summary>
        /// <typeparam name="T">Object type (must not be string) / 对象类型（不能是 string）</typeparam>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendJsonAsync<T>(
            this T data,
            string connectionId,
            JsonSerializerOptions options = null,
            Encoding encoding = null)
            where T : class
        {
            return await SendAsync(connectionId, data, options, encoding);
        }

        /// <summary>
        /// Send JSON object to multiple connections - extension method for convenient usage (excludes string type)
        /// 向多个连接发送 JSON 对象 - 扩展方法（便于使用，排除 string 类型）
        /// </summary>
        /// <typeparam name="T">Object type (must not be string) / 对象类型（不能是 string）</typeparam>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendJsonAsync<T>(
            this T data,
            IEnumerable<string> connectionIds,
            JsonSerializerOptions options = null,
            Encoding encoding = null)
            where T : class
        {
            return await SendAsync(connectionIds, data, options, encoding);
        }

        #endregion
    }



}