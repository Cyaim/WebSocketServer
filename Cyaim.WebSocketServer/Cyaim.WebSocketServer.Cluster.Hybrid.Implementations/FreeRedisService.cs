using System;
using System.Collections.Generic;
using System.Linq;
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
        }

        /// <summary>
        /// Connect to Redis / 连接到 Redis
        /// </summary>
        public async Task ConnectAsync()
        {
            if (_redis != null && _redis.IsConnected)
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
            if (_redis != null)
            {
                try
                {
                    // Unsubscribe from all channels / 取消订阅所有通道
                    foreach (var channel in _subscriptions.Keys.ToList())
                    {
                        await UnsubscribeAsync(channel);
                    }

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
            if (_redis == null || !_redis.IsConnected)
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
            if (_redis == null || !_redis.IsConnected)
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
            if (_redis == null || !_redis.IsConnected)
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
            if (_redis == null || !_redis.IsConnected)
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
            if (_redis == null || !_redis.IsConnected)
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
            if (_redis == null || !_redis.IsConnected)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            var exists = _redis.Exists(key) > 0;
            return await Task.FromResult(exists);
        }

        /// <summary>
        /// Subscribe to a channel / 订阅通道
        /// </summary>
        public async Task SubscribeAsync(string channel, Func<string, string, Task> handler)
        {
            if (_redis == null || !_redis.IsConnected)
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
            Task.Run(() =>
            {
                try
                {
                    _redis.Subscribe(channel, (ch, msg) =>
                    {
                        try
                        {
                            handler(ch, msg).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error handling message from channel {ch}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error subscribing to channel {channel}");
                    _subscriptions.Remove(channel);
                }
            });

            await Task.CompletedTask;
        }

        /// <summary>
        /// Unsubscribe from a channel / 取消订阅通道
        /// </summary>
        public async Task UnsubscribeAsync(string channel)
        {
            if (_redis == null || !_redis.IsConnected)
            {
                return;
            }

            try
            {
                _redis.UnSubscribe(channel);
                _subscriptions.Remove(channel);
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
            if (_redis == null || !_redis.IsConnected)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            _redis.Publish(channel, message);
            await Task.CompletedTask;
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
        }
    }
}

