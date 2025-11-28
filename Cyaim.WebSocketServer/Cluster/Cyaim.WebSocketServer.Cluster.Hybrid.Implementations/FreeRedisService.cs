using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Microsoft.Extensions.Logging;
using FreeRedis;

namespace Cyaim.WebSocketServer.Cluster.Hybrid.Implementations
{
    /// <summary>
    /// FreeRedis implementation of IRedisService
    /// FreeRedis 的 IRedisService 实现
    /// </summary>
    public class FreeRedisService : IRedisService
    {
        private readonly ILogger<FreeRedisService> _logger;
        private readonly string _connectionString;
        private RedisClient _redis;
        private readonly Dictionary<string, Func<string, string, Task>> _subscriptions;
        private readonly Dictionary<string, Task> _subscriptionTasks;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private bool _disposed = false;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="connectionString">Redis connection string / Redis 连接字符串</param>
        public FreeRedisService(ILogger<FreeRedisService> logger, string connectionString)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _subscriptions = new Dictionary<string, Func<string, string, Task>>();
            _subscriptionTasks = new Dictionary<string, Task>();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Connect to Redis / 连接到 Redis
        /// </summary>
        public async Task ConnectAsync()
        {
            if (_redis != null)
            {
                return;
            }

            try
            {
                _redis = new RedisClient(_connectionString);
                _logger.LogInformation("Connected to Redis using FreeRedis");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to Redis");
                throw;
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Disconnect from Redis / 断开 Redis 连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            // Cancel all subscriptions / 取消所有订阅
            _cancellationTokenSource.Cancel();

            if (_redis != null)
            {
                try
                {
                    // Unsubscribe from all channels / 取消订阅所有通道
                    foreach (var channel in _subscriptions.Keys.ToList())
                    {
                        await UnsubscribeAsync(channel);
                    }

                    // Wait for subscription tasks to complete (with timeout) / 等待订阅任务完成（带超时）
                    var tasks = _subscriptionTasks.Values.ToArray();
                    if (tasks.Length > 0)
                    {
                        try
                        {
                            await WaitWithTimeoutAsync(Task.WhenAll(tasks), TimeSpan.FromSeconds(5));
                        }
                        catch (TimeoutException)
                        {
                            _logger.LogWarning("Timeout waiting for subscription tasks to complete");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error waiting for subscription tasks to complete");
                        }
                    }

                    _subscriptionTasks.Clear();

                    _redis.Dispose();
                    _redis = null;
                    _logger.LogInformation("Disconnected from Redis");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disconnecting from Redis");
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Set a key-value pair with expiration / 设置键值对并设置过期时间
        /// </summary>
        public async Task SetAsync(string key, string value, TimeSpan? expiration = null)
        {
            if (_redis == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            if (expiration.HasValue)
            {
                _redis.Set(key, value, expiration.Value);
            }
            else
            {
                _redis.Set(key, value);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Get value by key / 根据键获取值
        /// </summary>
        public async Task<string> GetAsync(string key)
        {
            if (_redis == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            var value = _redis.Get(key);
            return await Task.FromResult(value);
        }

        /// <summary>
        /// Delete a key / 删除键
        /// </summary>
        public async Task DeleteAsync(string key)
        {
            if (_redis == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            _redis.Del(key);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Get all keys matching pattern / 获取所有匹配模式的键
        /// </summary>
        public async Task<List<string>> GetKeysAsync(string pattern)
        {
            if (_redis == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            var keys = _redis.Keys(pattern);
            return await Task.FromResult(keys?.ToList() ?? new List<string>());
        }

        /// <summary>
        /// Get all values for keys matching pattern / 获取所有匹配模式的键的值
        /// </summary>
        public async Task<Dictionary<string, string>> GetValuesAsync(string pattern)
        {
            var keys = await GetKeysAsync(pattern);
            var result = new Dictionary<string, string>();

            foreach (var key in keys)
            {
                try
                {
                    var value = await GetAsync(key);
                    if (value != null)
                    {
                        result[key] = value;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to get value for key {key}");
                }
            }

            return result;
        }

        /// <summary>
        /// Set expiration for a key / 为键设置过期时间
        /// </summary>
        public async Task SetExpirationAsync(string key, TimeSpan expiration)
        {
            if (_redis == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            _redis.Expire(key, (int)expiration.TotalSeconds);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Check if key exists / 检查键是否存在
        /// </summary>
        public async Task<bool> ExistsAsync(string key)
        {
            if (_redis == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            return await _redis.ExistsAsync(key);
        }

        /// <summary>
        /// Subscribe to a channel / 订阅通道
        /// </summary>
        public async Task SubscribeAsync(string channel, Func<string, string, Task> handler)
        {
            if (_redis == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            if (_subscriptions.ContainsKey(channel))
            {
                _logger.LogWarning($"Already subscribed to channel {channel}");
                return;
            }

            _subscriptions[channel] = handler;

            // Subscribe in background thread (FreeRedis Subscribe is blocking) / 在后台线程订阅（FreeRedis Subscribe 是阻塞的）
            var subscriptionTask = Task.Run(() =>
            {
                try
                {
                    _redis.Subscribe(channel, (ch, msg) =>
                    {
                        // Check cancellation token / 检查取消令牌
                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            return;
                        }

                        try
                        {
                            // Convert message from object to string / 将消息从 object 转换为 string
                            var messageStr = msg?.ToString() ?? string.Empty;
                            handler(ch, messageStr).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected when shutting down / 关闭时预期的异常
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error handling message from channel {ch}");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    // Expected when shutting down / 关闭时预期的异常
                    _logger.LogDebug($"Subscription to channel {channel} was cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error subscribing to channel {channel}");
                    _subscriptions.Remove(channel);
                }
            }, _cancellationTokenSource.Token);

            _subscriptionTasks[channel] = subscriptionTask;

            await Task.CompletedTask;
        }

        /// <summary>
        /// Unsubscribe from a channel / 取消订阅通道
        /// </summary>
        public async Task UnsubscribeAsync(string channel)
        {
            if (_redis == null)
            {
                return;
            }

            try
            {
                _redis.UnSubscribe(channel);
                _subscriptions.Remove(channel);

                // Wait for subscription task to complete if it exists / 如果存在订阅任务，等待其完成
                if (_subscriptionTasks.TryGetValue(channel, out var task))
                {
                    try
                    {
                        await WaitWithTimeoutAsync(task, TimeSpan.FromSeconds(2));
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning($"Timeout waiting for subscription task to complete for channel {channel}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Error waiting for subscription task to complete for channel {channel}");
                    }
                    finally
                    {
                        _subscriptionTasks.Remove(channel);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error unsubscribing from channel {channel}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Publish message to a channel / 向通道发布消息
        /// </summary>
        public async Task PublishAsync(string channel, string message)
        {
            if (_redis == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            _redis.Publish(channel, message);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Wait for task with timeout (compatible with .NET Standard 2.1) / 等待任务完成（带超时，兼容 .NET Standard 2.1）
        /// </summary>
        private static async Task WaitWithTimeoutAsync(Task task, TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource())
            {
                var delayTask = Task.Delay(timeout, cts.Token);
                var completedTask = await Task.WhenAny(task, delayTask);

                if (completedTask == delayTask)
                {
                    // Timeout occurred / 超时
                    throw new TimeoutException($"Task did not complete within {timeout.TotalSeconds} seconds");
                }

                // Cancel the delay task to avoid unnecessary delay / 取消延迟任务以避免不必要的延迟
                cts.Cancel();

                // Re-throw any exception from the original task / 重新抛出原始任务的任何异常
                await task;
            }
        }

        /// <summary>
        /// Dispose / 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DisconnectAsync().GetAwaiter().GetResult();
            _cancellationTokenSource.Dispose();
        }
    }
}

