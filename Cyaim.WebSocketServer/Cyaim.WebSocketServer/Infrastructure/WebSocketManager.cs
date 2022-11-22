using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// WebSocket operation method
    /// </summary>
    public class WebSocketManager
    {
        /// <summary>
        /// 发送数据
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
        /// 发送文本数据，不使用缓冲区
        /// </summary>
        /// <param name="data"></param>
        /// <param name="encoding"></param>
        /// <param name="socket"></param>
        /// <returns></returns>
        public static async Task SendAsync(string data, Encoding encoding = null, params WebSocket[] socket)
        {
            if (string.IsNullOrEmpty(data) || socket == null || socket.LongLength < 1)
            {
                return;
            }
            if (encoding == null)
            {
                encoding = Encoding.UTF8;
            }
            await SendAsync(encoding.GetBytes(data), WebSocketMessageType.Text, true, CancellationToken.None, socket);
        }

        /// <summary>
        /// 序列化并发送文本数据，不使用缓冲区
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="socket"></param>
        /// <returns></returns>
        public static async Task SendAsync<T>(T data, params WebSocket[] socket)
        {
            try
            {
                if (data == null || socket == null || socket.LongLength < 1)
                {
                    return;
                }

                await SendAsync(JsonConvert.SerializeObject(data), Encoding.UTF8, socket);
            }
            catch (Exception)
            {

                throw;
            }
        }
    }
}
