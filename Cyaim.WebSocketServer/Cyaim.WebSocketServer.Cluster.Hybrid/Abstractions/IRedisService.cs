using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions
{
    /// <summary>
    /// Redis service abstraction for node discovery and state sharing
    /// Redis 服务抽象，用于节点发现和状态共享
    /// </summary>
    public interface IRedisService : IDisposable
    {
        /// <summary>
        /// Connect to Redis / 连接到 Redis
        /// </summary>
        Task ConnectAsync();

        /// <summary>
        /// Disconnect from Redis / 断开 Redis 连接
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Set a key-value pair with expiration / 设置键值对并设置过期时间
        /// </summary>
        /// <param name="key">Key / 键</param>
        /// <param name="value">Value / 值</param>
        /// <param name="expiration">Expiration time / 过期时间</param>
        Task SetAsync(string key, string value, TimeSpan? expiration = null);

        /// <summary>
        /// Get value by key / 根据键获取值
        /// </summary>
        /// <param name="key">Key / 键</param>
        /// <returns>Value or null if not found / 值，如果不存在则返回 null</returns>
        Task<string> GetAsync(string key);

        /// <summary>
        /// Delete a key / 删除键
        /// </summary>
        /// <param name="key">Key / 键</param>
        Task DeleteAsync(string key);

        /// <summary>
        /// Get all keys matching pattern / 获取所有匹配模式的键
        /// </summary>
        /// <param name="pattern">Pattern / 模式</param>
        /// <returns>List of keys / 键列表</returns>
        Task<List<string>> GetKeysAsync(string pattern);

        /// <summary>
        /// Get all values for keys matching pattern / 获取所有匹配模式的键的值
        /// </summary>
        /// <param name="pattern">Pattern / 模式</param>
        /// <returns>Dictionary of key-value pairs / 键值对字典</returns>
        Task<Dictionary<string, string>> GetValuesAsync(string pattern);

        /// <summary>
        /// Set expiration for a key / 为键设置过期时间
        /// </summary>
        /// <param name="key">Key / 键</param>
        /// <param name="expiration">Expiration time / 过期时间</param>
        Task SetExpirationAsync(string key, TimeSpan expiration);

        /// <summary>
        /// Check if key exists / 检查键是否存在
        /// </summary>
        /// <param name="key">Key / 键</param>
        /// <returns>True if exists, false otherwise / 存在返回 true，否则返回 false</returns>
        Task<bool> ExistsAsync(string key);

        /// <summary>
        /// Subscribe to a channel / 订阅通道
        /// </summary>
        /// <param name="channel">Channel name / 通道名称</param>
        /// <param name="handler">Message handler / 消息处理器</param>
        Task SubscribeAsync(string channel, Func<string, string, Task> handler);

        /// <summary>
        /// Unsubscribe from a channel / 取消订阅通道
        /// </summary>
        /// <param name="channel">Channel name / 通道名称</param>
        Task UnsubscribeAsync(string channel);

        /// <summary>
        /// Publish message to a channel / 向通道发布消息
        /// </summary>
        /// <param name="channel">Channel name / 通道名称</param>
        /// <param name="message">Message / 消息</param>
        Task PublishAsync(string channel, string message);
    }
}

