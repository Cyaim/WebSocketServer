using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using MessagePack;

namespace Cyaim.WebSocketServer.MessagePack
{
    /// <summary>
    /// MessagePack serialization extensions for WebSocket
    /// MessagePack 序列化扩展方法
    /// </summary>
    public static class MessagePackExtensions
    {
        /// <summary>
        /// Send MessagePack serialized data to WebSocket
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
            MessagePackSerializerOptions options = null,
            WebSocketMessageType messageType = WebSocketMessageType.Binary,
            CancellationToken? cancellationToken = null,
            TimeSpan? timeout = null,
            int sendBufferSize = 4 * 1024,
            params WebSocket[] socket)
        {
            if (data == null || socket == null || socket.LongLength < 1)
            {
                return;
            }

            options ??= MessagePackSerializerOptions.Standard;
            var serializedData = MessagePackSerializer.Serialize(data, options);
            await WebSocketManager.SendLocalAsync(serializedData, messageType, serializedData.Length <= sendBufferSize, cancellationToken: cancellationToken ?? CancellationToken.None, timeout, sendBufferSize: (uint)sendBufferSize, sockets: socket);
        }

        /// <summary>
        /// Send MessagePack serialized data to WebSocket
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="socket"></param>
        /// <param name="data"></param>
        /// <param name="options"></param>
        /// <param name="messageType"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="timeout"></param>
        /// <param name="sendBufferSize"></param>
        /// <returns></returns>
        public static async Task SendAsync<T>(
            this WebSocket socket,
            T data,
            MessagePackSerializerOptions options = null,
            WebSocketMessageType messageType = WebSocketMessageType.Binary,
            CancellationToken? cancellationToken = null,
            TimeSpan? timeout = null,
            int sendBufferSize = 4 * 1024)
        {
            if (data == null || socket == null)
            {
                return;
            }

            options ??= MessagePackSerializerOptions.Standard;
            var serializedData = MessagePackSerializer.Serialize(data, options);
            await WebSocketManager.SendLocalAsync(serializedData, messageType, serializedData.Length <= sendBufferSize, cancellationToken: cancellationToken ?? CancellationToken.None, timeout, sendBufferSize: (uint)sendBufferSize, sockets: socket);
        }

        #region ClusterManager Extensions / 集群管理器扩展

        /// <summary>
        /// Route object as MessagePack binary message to a connection (supports cross-node)
        /// 将对象序列化为 MessagePack 二进制消息路由到连接（支持跨节点）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="clusterManager">Cluster manager instance / 集群管理器实例</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">MessagePack serializer options / MessagePack 序列化选项</param>
        /// <returns>True if routed successfully / 路由成功返回 true</returns>
        public static async Task<bool> RouteMessagePackAsync<T>(
            this ClusterManager clusterManager,
            string connectionId,
            T data,
            MessagePackSerializerOptions options = null)
        {
            if (clusterManager == null)
            {
                throw new ArgumentNullException(nameof(clusterManager));
            }

            if (data == null)
            {
                return false;
            }

            options ??= MessagePackSerializerOptions.Standard;
            var bytes = MessagePackSerializer.Serialize(data, options);
            return await clusterManager.RouteMessageAsync(connectionId, bytes, (int)WebSocketMessageType.Binary);
        }

        /// <summary>
        /// Route object as MessagePack binary message to multiple connections (supports cross-node)
        /// 将对象序列化为 MessagePack 二进制消息路由到多个连接（支持跨节点）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="clusterManager">Cluster manager instance / 集群管理器实例</param>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">MessagePack serializer options / MessagePack 序列化选项</param>
        /// <returns>Dictionary of connection ID to routing result / 连接ID到路由结果的字典</returns>
        public static async Task<Dictionary<string, bool>> RouteMessagePacksAsync<T>(
            this ClusterManager clusterManager,
            IEnumerable<string> connectionIds,
            T data,
            MessagePackSerializerOptions options = null)
        {
            if (clusterManager == null)
            {
                throw new ArgumentNullException(nameof(clusterManager));
            }

            if (data == null)
            {
                return new Dictionary<string, bool>();
            }

            options ??= MessagePackSerializerOptions.Standard;
            var bytes = MessagePackSerializer.Serialize(data, options);
            return await clusterManager.RouteMessagesAsync(connectionIds, bytes, (int)WebSocketMessageType.Binary);
        }

        #endregion
    }
}

