using System;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions
{
    /// <summary>
    /// Message queue service abstraction for routing WebSocket messages
    /// 消息队列服务抽象，用于路由 WebSocket 消息
    /// </summary>
    public interface IMessageQueueService : IDisposable
    {
        /// <summary>
        /// Connect to message queue / 连接到消息队列
        /// </summary>
        Task ConnectAsync();

        /// <summary>
        /// Verify connection is ready (optional but recommended) / 验证连接已就绪（可选但推荐）
        /// </summary>
        /// <remarks>
        /// Implementations should ensure the underlying connection and channel are usable.
        /// 实现应确保底层连接和通道可用。
        /// </remarks>
        Task VerifyConnectionAsync();

        /// <summary>
        /// Disconnect from message queue / 断开消息队列连接
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Declare an exchange / 声明交换机
        /// </summary>
        /// <param name="exchangeName">Exchange name / 交换机名称</param>
        /// <param name="exchangeType">Exchange type / 交换机类型</param>
        /// <param name="durable">Whether exchange is durable / 交换机是否持久化</param>
        Task DeclareExchangeAsync(string exchangeName, string exchangeType, bool durable = true);

        /// <summary>
        /// Declare a queue / 声明队列
        /// </summary>
        /// <param name="queueName">Queue name / 队列名称</param>
        /// <param name="durable">Whether queue is durable / 队列是否持久化</param>
        /// <param name="exclusive">Whether queue is exclusive / 队列是否独占</param>
        /// <param name="autoDelete">Whether queue auto-deletes / 队列是否自动删除</param>
        /// <returns>Queue name / 队列名称</returns>
        Task<string> DeclareQueueAsync(string queueName, bool durable = false, bool exclusive = false, bool autoDelete = true);

        /// <summary>
        /// Bind queue to exchange / 将队列绑定到交换机
        /// </summary>
        /// <param name="queueName">Queue name / 队列名称</param>
        /// <param name="exchangeName">Exchange name / 交换机名称</param>
        /// <param name="routingKey">Routing key / 路由键</param>
        Task BindQueueAsync(string queueName, string exchangeName, string routingKey);

        /// <summary>
        /// Publish message to exchange / 向交换机发布消息
        /// </summary>
        /// <param name="exchangeName">Exchange name / 交换机名称</param>
        /// <param name="routingKey">Routing key / 路由键</param>
        /// <param name="message">Message body / 消息体</param>
        /// <param name="properties">Message properties / 消息属性</param>
        Task PublishAsync(string exchangeName, string routingKey, byte[] message, MessageProperties properties = null);

        /// <summary>
        /// Consume messages from queue / 从队列消费消息
        /// </summary>
        /// <param name="queueName">Queue name / 队列名称</param>
        /// <param name="handler">Message handler / 消息处理器</param>
        /// <param name="autoAck">Whether to auto-acknowledge / 是否自动确认</param>
        /// <param name="currentNodeId">
        /// Current node id, for implementations that want to early-filter self messages using headers (e.g. FromNodeId).
        /// 当前节点 ID，供实现使用消息头（例如 FromNodeId）进行早期自发消息过滤。
        /// </param>
        Task ConsumeAsync(string queueName, Func<byte[], MessageProperties, Task<bool>> handler, bool autoAck = false, string currentNodeId = null);

        /// <summary>
        /// Acknowledge message / 确认消息
        /// </summary>
        /// <param name="deliveryTag">Delivery tag / 投递标签</param>
        Task AckAsync(ulong deliveryTag);

        /// <summary>
        /// Reject message / 拒绝消息
        /// </summary>
        /// <param name="deliveryTag">Delivery tag / 投递标签</param>
        /// <param name="requeue">Whether to requeue / 是否重新入队</param>
        Task RejectAsync(ulong deliveryTag, bool requeue = false);

        /// <summary>
        /// Get queue consumer count / 获取队列消费者数量
        /// </summary>
        /// <param name="queueName">Queue name / 队列名称</param>
        /// <returns>Consumer count / 消费者数量</returns>
        Task<uint> GetQueueConsumerCountAsync(string queueName);
    }

    /// <summary>
    /// Message properties / 消息属性
    /// </summary>
    public class MessageProperties
    {
        /// <summary>
        /// Message ID / 消息 ID
        /// </summary>
        public string MessageId { get; set; }

        /// <summary>
        /// Correlation ID / 关联 ID
        /// </summary>
        public string CorrelationId { get; set; }

        /// <summary>
        /// Reply to queue / 回复队列
        /// </summary>
        public string ReplyTo { get; set; }

        /// <summary>
        /// Timestamp / 时间戳
        /// </summary>
        public DateTime? Timestamp { get; set; }

        /// <summary>
        /// Headers / 头信息
        /// </summary>
        public System.Collections.Generic.Dictionary<string, object> Headers { get; set; }

        /// <summary>
        /// Delivery tag (for acknowledgment) / 投递标签（用于确认）
        /// </summary>
        public ulong DeliveryTag { get; set; }

        /// <summary>
        /// Routing key (for RabbitMQ) / 路由键（用于 RabbitMQ）
        /// </summary>
        public string RoutingKey { get; set; }

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public MessageProperties()
        {
            Headers = new System.Collections.Generic.Dictionary<string, object>();
        }
    }
}

