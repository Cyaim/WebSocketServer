using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure;
using MessagePack;

namespace Cyaim.WebSocketServer.MessagePack
{
    /// <summary>
    /// MessagePack serialization extensions for WebSocket
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
            await WebSocketManager.SendAsync(serializedData, messageType, serializedData.Length <= sendBufferSize, cancellationToken: cancellationToken ?? CancellationToken.None, timeout, sendBufferSize: (uint)sendBufferSize, sockets: socket);
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
            await WebSocketManager.SendAsync(serializedData, messageType, serializedData.Length <= sendBufferSize, cancellationToken: cancellationToken ?? CancellationToken.None, timeout, sendBufferSize: (uint)sendBufferSize, sockets: socket);
        }
    }
}

