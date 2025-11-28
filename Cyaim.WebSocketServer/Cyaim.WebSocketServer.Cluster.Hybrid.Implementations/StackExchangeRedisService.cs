using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Cyaim.WebSocketServer.Cluster.Hybrid.Implementations
{
    /// <summary>
    /// StackExchange.Redis implementation of IRedisService
    /// StackExchange.Redis 的 IRedisService 实现
    /// </summary>
    public class StackExchangeRedisService : IRedisService
    {
        private readonly ILogger<StackExchangeRedisService> _logger;
        private readonly string _connectionString;
        private ConnectionMultiplexer _redis;
        private ISubscriber _subscriber;
        private readonly Dictionary<string, Func<string, string, Task>> _subscriptions;
        private bool _disposed = false;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="connectionString">Redis connection string / Redis 连接字符串</param>
        public StackExchangeRedisService(ILogger<StackExchangeRedisService> logger, string connectionString)
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
                _redis = await ConnectionMultiplexer.ConnectAsync(_connectionString);
                _subscriber = _redis.GetSubscriber();
                _logger.LogInformation("Connected to Redis");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to Redis");
                throw;
            }
        }

        /// <summary>
        /// Disconnect from Redis / 断开 Redis 连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_redis != null)
            {
                await _redis.CloseAsync();
                _redis?.Dispose();
                _redis = null;
                _subscriber = null;
                _logger.LogInformation("Disconnected from Redis");
            }
        }

        /// <summary>
        /// Set a key-value pair with expiration / 设置键值对并设置过期时间
        /// </summary>
        public async Task SetAsync(string key, string value, TimeSpan? expiration = null)
        {
            var db = _redis?.GetDatabase();
            if (db == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            if (expiration.HasValue)
            {
                await db.StringSetAsync(key, value, expiration.Value);
            }
            else
            {
                await db.StringSetAsync(key, value);
            }
        }

        /// <summary>
        /// Get value by key / 根据键获取值
        /// </summary>
        public async Task<string> GetAsync(string key)
        {
            var db = _redis?.GetDatabase();
            if (db == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            var value = await db.StringGetAsync(key);
            return value.HasValue ? value.ToString() : null;
        }

        /// <summary>
        /// Delete a key / 删除键
        /// </summary>
        public async Task DeleteAsync(string key)
        {
            var db = _redis?.GetDatabase();
            if (db == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            await db.KeyDeleteAsync(key);
        }

        /// <summary>
        /// Get all keys matching pattern / 获取所有匹配模式的键
        /// </summary>
        public async Task<List<string>> GetKeysAsync(string pattern)
        {
            var server = _redis?.GetServer(_redis.GetEndPoints().First());
            if (server == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            var keys = new List<string>();
            await foreach (var key in server.KeysAsync(pattern: pattern))
            {
                keys.Add(key.ToString());
            }

            return keys;
        }

        /// <summary>
        /// Get all values for keys matching pattern / 获取所有匹配模式的键的值
        /// </summary>
        public async Task<Dictionary<string, string>> GetValuesAsync(string pattern)
        {
            var keys = await GetKeysAsync(pattern);
            var db = _redis?.GetDatabase();
            if (db == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            var result = new Dictionary<string, string>();
            foreach (var key in keys)
            {
                var value = await db.StringGetAsync(key);
                if (value.HasValue)
                {
                    result[key] = value.ToString();
                }
            }

            return result;
        }

        /// <summary>
        /// Set expiration for a key / 为键设置过期时间
        /// </summary>
        public async Task SetExpirationAsync(string key, TimeSpan expiration)
        {
            var db = _redis?.GetDatabase();
            if (db == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            await db.KeyExpireAsync(key, expiration);
        }

        /// <summary>
        /// Check if key exists / 检查键是否存在
        /// </summary>
        public async Task<bool> ExistsAsync(string key)
        {
            var db = _redis?.GetDatabase();
            if (db == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            return await db.KeyExistsAsync(key);
        }

        /// <summary>
        /// Subscribe to a channel / 订阅通道
        /// </summary>
        public async Task SubscribeAsync(string channel, Func<string, string, Task> handler)
        {
            if (_subscriber == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            _subscriptions[channel] = handler;

            await _subscriber.SubscribeAsync(channel, (ch, message) =>
            {
                handler(ch, message).GetAwaiter().GetResult();
            });
        }

        /// <summary>
        /// Unsubscribe from a channel / 取消订阅通道
        /// </summary>
        public async Task UnsubscribeAsync(string channel)
        {
            if (_subscriber == null)
            {
                return;
            }

            await _subscriber.UnsubscribeAsync(channel);
            _subscriptions.Remove(channel);
        }

        /// <summary>
        /// Publish message to a channel / 向通道发布消息
        /// </summary>
        public async Task PublishAsync(string channel, string message)
        {
            if (_subscriber == null)
            {
                throw new InvalidOperationException("Redis is not connected");
            }

            await _subscriber.PublishAsync(channel, message);
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

