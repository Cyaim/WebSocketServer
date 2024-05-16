using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// WebSocket operation method
    /// </summary>
    public static class WebSocketManager
    {
        /// <summary>
        /// Default send encoding
        /// </summary>
        public static Encoding Encoding { get; set; } = Encoding.UTF8;
        /// <summary>
        /// JsonSerialiazerOptions
        /// </summary>
        public static JsonSerializerOptions DefaultMvcJsonSerialiazerOptions { get; set; } = new JsonSerializerOptions
        {
            // 设置为 true 以忽略属性名称的大小写
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };

        /// <summary>
        /// Send data without buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="messageType"></param>
        /// <param name="endOfMessage"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="socket"></param>
        /// <returns></returns>
        public static async Task SendAsync(MemoryStream buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken, params WebSocket[] socket)
        {
            if (socket == null || socket.LongLength < 1)
            {
                return;
            }
            ParallelLoopResult result = Parallel.ForEach(socket, async (s, state) =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        state.Stop();
                        return;
                    }
                    if (s.State == WebSocketState.Open)
                    {
                        //while (sent < data.Length)
                        //{
                        //    int length = Math.Min(bufferSize, data.Length - sent);
                        //    ArraySegment<byte> segment = new ArraySegment<byte>(data, sent, length);
                        //    await webSocket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                        //    sent += length;
                        //}

                        await s.SendAsync(buffer.GetBuffer(), messageType, endOfMessage, cancellationToken);
                    }

                }
                catch (AggregateException age)
                {
                    foreach (var item in age.InnerExceptions)
                    {
                        Console.WriteLine(item.Message);
                    }
                }
                catch (Exception)
                {

                    throw;
                }
            });

            while (!result.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

        }

        /// <summary>
        /// Send data without buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="messageType"></param>
        /// <param name="endOfMessage"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="socket"></param>
        /// <returns></returns>
        public static async Task SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken, params WebSocket[] socket)
        {
            if (socket == null || socket.LongLength < 1)
            {
                return;
            }
            ParallelLoopResult result = Parallel.ForEach(socket, async (s, state) =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        state.Stop();
                        return;
                    }
                    if (s.State == WebSocketState.Open)
                    {
                        await s.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
                    }

                }
                catch (AggregateException age)
                {
                    foreach (var item in age.InnerExceptions)
                    {
                        Console.WriteLine(item.Message);
                    }
                }
                catch (Exception)
                {

                    throw;
                }
            });

            while (!result.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

        }

        /// <summary>
        /// Send data without buffer
        /// </summary>
        /// <param name="data"></param>
        /// <param name="messageType"></param>
        /// <param name="encoding"></param>
        /// <param name="socket"></param>
        /// <returns></returns>
        public static async Task SendAsync(string data, WebSocketMessageType messageType = WebSocketMessageType.Text, Encoding encoding = null, params WebSocket[] socket)
        {
            if (string.IsNullOrEmpty(data) || socket == null || socket.LongLength < 1)
            {
                return;
            }
            if (encoding == null)
            {
                encoding = Encoding;
            }
            await SendAsync(encoding.GetBytes(data), messageType, true, CancellationToken.None, socket);
        }


        /// <summary>
        /// Sending serialized model text data without using a buffer
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="messageType"></param>
        /// <param name="socket"></param>
        /// <returns></returns>
        public static async Task SendAsync<T>(this T data, WebSocketMessageType messageType = WebSocketMessageType.Text, params WebSocket[] socket)
        {
            try
            {
                if (data == null || socket == null || socket.LongLength < 1)
                {
                    return;
                }

                await SendAsync(JsonSerializer.Serialize(data, DefaultMvcJsonSerialiazerOptions), messageType, Encoding, socket);
            }
            catch (Exception)
            {

                throw;
            }
        }

        /// <summary>
        /// Sending serialized model text data without using a buffer
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="socket"></param>
        /// <param name="data"></param>
        /// <param name="messageType"></param>
        /// <returns></returns>
        public static async Task SendAsync<T>(this WebSocket socket, T data, WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            try
            {
                if (data == null || socket == null)
                {
                    return;
                }

                await SendAsync(JsonSerializer.Serialize(data, DefaultMvcJsonSerialiazerOptions), messageType, Encoding, socket);
            }
            catch (Exception)
            {

                throw;
            }
        }


    }
}
