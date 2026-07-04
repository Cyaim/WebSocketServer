using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
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
        /// Per-socket send gate. WebSocket allows only one outstanding SendAsync per instance,
        /// so concurrent callers targeting the same socket serialize here instead of through
        /// a process-wide queue. Entries are released automatically when the socket is collected.
        /// 每个 socket 的发送门闩。WebSocket 同一实例只允许一个未完成的 SendAsync，
        /// 并发发送同一 socket 时在此串行化（替代旧的全局单消费者队列）。socket 被回收后条目自动释放。
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim> SendLocks = new System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim>();

        private static SemaphoreSlim GetSendLock(WebSocket socket)
        {
            return SendLocks.GetValue(socket, static _ => new SemaphoreSlim(1, 1));
        }

        #region Send core

        /// <summary>
        /// Send a buffer to a single socket, holding the socket's send gate for the whole message
        /// so multi-frame sends never interleave with other senders.
        /// 向单个 socket 发送缓冲区数据，整条消息期间持有该 socket 的发送门闩，避免多帧交叠。
        /// </summary>
        private static async Task SendBufferCoreAsync(WebSocket socket, ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, CancellationToken cancellationToken)
        {
            var gate = GetSendLock(socket);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }
                if (sendAtOnce || buffer.Length <= sendBufferSize)
                {
                    await socket.SendAsync(buffer, messageType, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendBufferedDataInBatchesAsync(socket, messageType, buffer, sendBufferSize, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Send stream content to a single socket under its send gate.
        /// 在发送门闩保护下向单个 socket 发送流数据。
        /// </summary>
        private static async Task SendStreamCoreAsync(WebSocket socket, Stream stream, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, CancellationToken cancellationToken)
        {
            var gate = GetSendLock(socket);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }
                if (sendAtOnce)
                {
                    await SendStreamDataAsync(socket, messageType, stream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendStreamDataInBatchesAsync(socket, messageType, stream, sendBufferSize, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Send a buffer to many sockets, bounded by <see cref="BatchProcessingWebsocketLimit"/> per wave.
        /// Individual socket failures are swallowed so one bad connection doesn't fail the batch.
        /// 向多个 socket 发送缓冲区数据，每波并发受 <see cref="BatchProcessingWebsocketLimit"/> 限制。
        /// 单个 socket 的失败被吞掉，避免一个坏连接影响整批。
        /// </summary>
        private static async Task SendBufferToManyAsync(WebSocket[] sockets, ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, CancellationToken cancellationToken)
        {
            List<Task> batch = new List<Task>(Math.Min(sockets.Length, BatchProcessingWebsocketLimit));
            for (int i = 0; i < sockets.Length; i++)
            {
                WebSocket socket = sockets[i];
                if (socket == null || socket.State != WebSocketState.Open)
                {
                    continue;
                }
                batch.Add(SendBufferCoreAsync(socket, buffer, messageType, sendAtOnce, sendBufferSize, cancellationToken));
                if (batch.Count >= BatchProcessingWebsocketLimit)
                {
                    try { await Task.WhenAll(batch).ConfigureAwait(false); } catch { }
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                try { await Task.WhenAll(batch).ConfigureAwait(false); } catch { }
            }
        }

        /// <summary>
        /// Await a send with an optional completion-wait timeout. On timeout the send keeps
        /// running detached (previous channel-based behavior) and its exception, if any, is observed.
        /// 等待发送完成，支持可选超时。超时后发送继续在后台执行（与旧的通道行为一致），异常会被观察以防进程崩溃。
        /// </summary>
        private static async Task AwaitWithTimeoutAsync(Task sendTask, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            if (timeout == null || timeout.Value == Timeout.InfiniteTimeSpan)
            {
                // 无超时：直接等待并让异常传播，调用方（如集群本地流路由）据此判断发送是否成功。
                // 多目标扇出在 SendBufferToManyAsync 内部已吞掉单 socket 失败，不会传播到这里。
                // No timeout: await directly and let exceptions propagate so callers (e.g. cluster
                // local stream routing) can detect send failure. Multi-target fan-out already
                // swallows per-socket faults inside SendBufferToManyAsync, so nothing propagates there.
                await sendTask.ConfigureAwait(false);
                return;
            }

            var completed = await Task.WhenAny(sendTask, Task.Delay(timeout.Value, cancellationToken)).ConfigureAwait(false);
            if (completed == sendTask)
            {
                try { await sendTask.ConfigureAwait(false); } catch { }
            }
            else
            {
                // Detached: observe faults to avoid unobserved task exceptions
                _ = sendTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
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
                // 按请求的 bufferSize 读取，租借的缓冲区可能大于请求大小
                // Read at the requested bufferSize: the rented buffer may be larger than requested
                while ((bytesRead = await stream.ReadAsync(buffer, 0, (int)bufferSize, cancellationToken)) > 0)
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

            Task sendTask;
            WebSocket single = sockets.Length == 1 ? sockets[0] : null;
            if (single != null)
            {
                if (single.State != WebSocketState.Open)
                {
                    return;
                }
                sendTask = SendStreamCoreAsync(single, sendStream, messageType, sendAtOnce, sendBufferSize, cancellationToken);
            }
            else
            {
                // Multiple targets cannot share one stream concurrently: buffer it once, then fan out
                // 多个目标不能并发共享同一个流：先一次性缓冲，再分发
                sendTask = SendStreamToManyAsync(sockets, sendStream, messageType, sendAtOnce, sendBufferSize, cancellationToken);
            }

            await AwaitWithTimeoutAsync(sendTask, timeout, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Buffer a stream once and fan it out to many sockets.
        /// 将流缓冲一次后分发给多个 socket。
        /// </summary>
        private static async Task SendStreamToManyAsync(WebSocket[] sockets, Stream stream, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, CancellationToken cancellationToken)
        {
            using var buffered = new MemoryStream();
            await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
            await SendBufferToManyAsync(sockets, buffered.GetBuffer().AsMemory(0, (int)buffered.Length), messageType, sendAtOnce, sendBufferSize, cancellationToken).ConfigureAwait(false);
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

            // Fast path: single open socket, no intermediate allocations
            // 快速路径：单个打开的 socket，无中间分配
            WebSocket single = sockets.Length == 1 ? sockets[0] : null;
            if (single != null)
            {
                if (single.State != WebSocketState.Open)
                {
                    throw new ArgumentNullException(nameof(sockets));
                }
                await AwaitWithTimeoutAsync(SendBufferCoreAsync(single, buffer, messageType, sendAtOnce, sendBufferSize, cancellationToken), timeout, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool anyOpen = false;
            for (int i = 0; i < sockets.Length; i++)
            {
                if (sockets[i] != null && sockets[i].State == WebSocketState.Open)
                {
                    anyOpen = true;
                    break;
                }
            }
            if (!anyOpen)
            {
                throw new ArgumentNullException(nameof(sockets));
            }

            await AwaitWithTimeoutAsync(SendBufferToManyAsync(sockets, buffer, messageType, sendAtOnce, sendBufferSize, cancellationToken), timeout, cancellationToken).ConfigureAwait(false);
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

            // Check if cluster is enabled / 检查是否启用集群
            var clusterManager = GlobalClusterCenter.ClusterManager;
            if (clusterManager != null)
            {
                // Use cluster routing / 使用集群路由
                var connectionIdsList = connectionIds.ToList();
                var connectionIdsArray = connectionIdsList.ToArray();

                var results = await clusterManager.RouteMessagesAsync(connectionIdsArray, data, (int)messageType);
                return results;
            }
            else
            {
                // Use local WebSocket / 使用本地 WebSocket
                var results = new Dictionary<string, bool>();
                var tasks = new List<Task>();
                foreach (var connectionId in connectionIds)
                {
                    if (string.IsNullOrEmpty(connectionId))
                    {
                        continue;
                    }
                    var webSocket = GetLocalWebSocket(connectionId);
                    if (webSocket != null && webSocket.State == WebSocketState.Open)
                    {
                        Task task = webSocket.SendAsync(
                             new ArraySegment<byte>(data),
                             messageType,
                             true,
                             CancellationToken.None);
                        tasks.Add(task);

                        results[connectionId] = true;
                    }
                    else
                    {
                        results[connectionId] = false;
                    }
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

        /// <summary>
        /// Send stream to connection(s) - extension method for convenient usage
        /// 向连接发送流 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="stream">Stream to send / 要发送的流</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="chunkSize">Chunk size in bytes (default: 64KB) / 块大小（字节，默认：64KB）</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendAsync(
            this Stream stream,
            string connectionId,
            WebSocketMessageType messageType = WebSocketMessageType.Binary,
            int chunkSize = 64 * 1024,
            CancellationToken cancellationToken = default)
        {
            if (stream == null || !stream.CanRead)
            {
                return false;
            }

            // Check if cluster is enabled / 检查是否启用集群
            var clusterManager = GlobalClusterCenter.ClusterManager;
            if (clusterManager != null)
            {
                // Use cluster routing / 使用集群路由
                return await clusterManager.RouteStreamAsync(connectionId, stream, messageType, chunkSize, cancellationToken);
            }
            else
            {
                // Use local WebSocket / 使用本地 WebSocket
                var webSocket = GetLocalWebSocket(connectionId);
                if (webSocket != null && webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await SendLocalAsync(stream, messageType, cancellationToken, timeout: null, sendAtOnce: false, sendBufferSize: (uint)chunkSize, sockets: webSocket);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Send stream to multiple connections - extension method for convenient usage
        /// 向多个连接发送流 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="stream">Stream to send / 要发送的流</param>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="chunkSize">Chunk size in bytes (default: 64KB) / 块大小（字节，默认：64KB）</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendAsync(
            this Stream stream,
            IEnumerable<string> connectionIds,
            WebSocketMessageType messageType = WebSocketMessageType.Binary,
            int chunkSize = 64 * 1024,
            CancellationToken cancellationToken = default)
        {
            if (stream == null || !stream.CanRead)
            {
                return new Dictionary<string, bool>();
            }

            // Check if cluster is enabled / 检查是否启用集群
            var clusterManager = GlobalClusterCenter.ClusterManager;
            if (clusterManager != null)
            {
                // Use cluster routing / 使用集群路由
                return await clusterManager.RouteStreamsAsync(connectionIds, stream, messageType, chunkSize, cancellationToken);
            }
            else
            {
                // Use local WebSocket / 使用本地 WebSocket
                var results = new Dictionary<string, bool>();
                var connectionIdList = connectionIds.Where(id => !string.IsNullOrEmpty(id)).ToList();

                if (connectionIdList.Count == 0)
                {
                    return results;
                }

                // For multiple connections, buffer the stream once into an immutable byte[].
                // 对于多个连接，将流一次性缓冲为不可变的 byte[]。
                // 关键：不能让多个并发任务共享同一个可变 MemoryStream 并各自重置 Position——
                // 并发读取会相互踩踏导致发给不同客户端的数据损坏；结果也不能并发写普通 Dictionary。
                // Critical: concurrent tasks must NOT share one mutable MemoryStream and reset its
                // Position — concurrent reads corrupt each other, delivering garbled data to clients;
                // and results must not be written to a plain Dictionary concurrently.
                if (connectionIdList.Count > 1)
                {
                    byte[] payload;
                    using (var memoryStream = new MemoryStream())
                    {
                        await stream.CopyToAsync(memoryStream, cancellationToken);
                        payload = memoryStream.ToArray();
                    }

                    var sendResults = await Task.WhenAll(connectionIdList.Select(async connectionId =>
                    {
                        var webSocket = GetLocalWebSocket(connectionId);
                        if (webSocket != null && webSocket.State == WebSocketState.Open)
                        {
                            try
                            {
                                await SendLocalAsync(new ReadOnlyMemory<byte>(payload), messageType, sendAtOnce: false, cancellationToken, timeout: null, sendBufferSize: (uint)chunkSize, sockets: webSocket);
                                return (connectionId, ok: true);
                            }
                            catch
                            {
                                return (connectionId, ok: false);
                            }
                        }
                        return (connectionId, ok: false);
                    }));

                    foreach (var (connectionId, ok) in sendResults)
                    {
                        results[connectionId] = ok;
                    }
                }
                else
                {
                    // Single connection - stream directly / 单个连接 - 直接流式传输
                    var connectionId = connectionIdList[0];
                    var webSocket = GetLocalWebSocket(connectionId);
                    if (webSocket != null && webSocket.State == WebSocketState.Open)
                    {
                        try
                        {
                            await SendLocalAsync(stream, messageType, cancellationToken, timeout: null, sendAtOnce: false, sendBufferSize: (uint)chunkSize, sockets: webSocket);
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
                }

                return results;
            }
        }

        #endregion
    }



}