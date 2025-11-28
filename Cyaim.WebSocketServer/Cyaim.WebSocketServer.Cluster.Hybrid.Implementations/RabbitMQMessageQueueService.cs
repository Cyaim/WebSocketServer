using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Cyaim.WebSocketServer.Cluster.Hybrid.Implementations
{
    /// <summary>
    /// RabbitMQ.Client implementation of IMessageQueueService
    /// RabbitMQ.Client 的 IMessageQueueService 实现
    /// </summary>
    public class RabbitMQMessageQueueService : IMessageQueueService
    {
        private readonly ILogger<RabbitMQMessageQueueService> _logger;
        private readonly string _connectionString;
        private IConnection _connection;
        private IModel _channel;
        private readonly Dictionary<string, EventingBasicConsumer> _consumers;
        private bool _disposed = false;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="connectionString">RabbitMQ connection string / RabbitMQ 连接字符串</param>
        public RabbitMQMessageQueueService(ILogger<RabbitMQMessageQueueService> logger, string connectionString)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _consumers = new Dictionary<string, EventingBasicConsumer>();
        }

        /// <summary>
        /// Connect to message queue / 连接到消息队列
        /// </summary>
        public async Task ConnectAsync()
        {
            if (_connection != null && _connection.IsOpen)
            {
                return;
            }

            try
            {
                var factory = new ConnectionFactory { Uri = new Uri(_connectionString) };
                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();
                _logger.LogInformation("Connected to RabbitMQ");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to RabbitMQ");
                throw;
            }
        }

        /// <summary>
        /// Disconnect from message queue / 断开消息队列连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_channel != null)
            {
                _channel.Close();
                _channel.Dispose();
                _channel = null;
            }

            if (_connection != null)
            {
                _connection.Close();
                _connection.Dispose();
                _connection = null;
                _logger.LogInformation("Disconnected from RabbitMQ");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Declare an exchange / 声明交换机
        /// </summary>
        public async Task DeclareExchangeAsync(string exchangeName, string exchangeType, bool durable = true)
        {
            if (_channel == null)
            {
                throw new InvalidOperationException("RabbitMQ is not connected");
            }

            _channel.ExchangeDeclare(exchangeName, exchangeType, durable: durable, autoDelete: false);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Declare a queue / 声明队列
        /// </summary>
        public async Task<string> DeclareQueueAsync(string queueName, bool durable = false, bool exclusive = false, bool autoDelete = true)
        {
            if (_channel == null)
            {
                throw new InvalidOperationException("RabbitMQ is not connected");
            }

            var result = _channel.QueueDeclare(
                queue: queueName,
                durable: durable,
                exclusive: exclusive,
                autoDelete: autoDelete,
                arguments: null);

            return await Task.FromResult(result.QueueName);
        }

        /// <summary>
        /// Bind queue to exchange / 将队列绑定到交换机
        /// </summary>
        public async Task BindQueueAsync(string queueName, string exchangeName, string routingKey)
        {
            if (_channel == null)
            {
                throw new InvalidOperationException("RabbitMQ is not connected");
            }

            _channel.QueueBind(queueName, exchangeName, routingKey);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Publish message to exchange / 向交换机发布消息
        /// </summary>
        public async Task PublishAsync(string exchangeName, string routingKey, byte[] message, MessageProperties properties = null)
        {
            if (_channel == null)
            {
                throw new InvalidOperationException("RabbitMQ is not connected");
            }

            var basicProperties = _channel.CreateBasicProperties();
            
            if (properties != null)
            {
                basicProperties.MessageId = properties.MessageId;
                basicProperties.CorrelationId = properties.CorrelationId;
                basicProperties.ReplyTo = properties.ReplyTo;
                
                if (properties.Timestamp.HasValue)
                {
                    basicProperties.Timestamp = new AmqpTimestamp(
                        (long)(properties.Timestamp.Value - new DateTime(1970, 1, 1)).TotalSeconds);
                }

                if (properties.Headers != null && properties.Headers.Count > 0)
                {
                    basicProperties.Headers = new Dictionary<string, object>();
                    foreach (var header in properties.Headers)
                    {
                        basicProperties.Headers[header.Key] = header.Value;
                    }
                }
            }

            _channel.BasicPublish(exchangeName, routingKey, basicProperties, message);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Consume messages from queue / 从队列消费消息
        /// </summary>
        public async Task ConsumeAsync(string queueName, Func<byte[], MessageProperties, Task<bool>> handler, bool autoAck = false)
        {
            if (_channel == null)
            {
                throw new InvalidOperationException("RabbitMQ is not connected");
            }

            if (_consumers.ContainsKey(queueName))
            {
                _logger.LogWarning($"Consumer for queue {queueName} already exists");
                return;
            }

            var consumer = new EventingBasicConsumer(_channel);
            _consumers[queueName] = consumer;

            consumer.Received += async (model, ea) =>
            {
                try
                {
                    var properties = new MessageProperties
                    {
                        MessageId = ea.BasicProperties.MessageId,
                        CorrelationId = ea.BasicProperties.CorrelationId,
                        ReplyTo = ea.BasicProperties.ReplyTo,
                        DeliveryTag = ea.DeliveryTag,
                        Headers = new Dictionary<string, object>()
                    };

                    if (ea.BasicProperties.Timestamp.UnixTime > 0)
                    {
                        properties.Timestamp = new DateTime(1970, 1, 1).AddSeconds(ea.BasicProperties.Timestamp.UnixTime);
                    }

                    if (ea.BasicProperties.Headers != null)
                    {
                        foreach (var header in ea.BasicProperties.Headers)
                        {
                            properties.Headers[header.Key.ToString()] = header.Value;
                        }
                    }

                    var success = await handler(ea.Body.ToArray(), properties);
                    
                    if (!autoAck)
                    {
                        if (success)
                        {
                            _channel.BasicAck(ea.DeliveryTag, false);
                        }
                        else
                        {
                            _channel.BasicNack(ea.DeliveryTag, false, true); // Requeue / 重新入队
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing message from queue {queueName}");
                    if (!autoAck)
                    {
                        _channel.BasicNack(ea.DeliveryTag, false, true);
                    }
                }
            };

            _channel.BasicConsume(queueName, autoAck, consumer);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Acknowledge message / 确认消息
        /// </summary>
        public async Task AckAsync(ulong deliveryTag)
        {
            if (_channel == null)
            {
                throw new InvalidOperationException("RabbitMQ is not connected");
            }

            _channel.BasicAck(deliveryTag, false);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Reject message / 拒绝消息
        /// </summary>
        public async Task RejectAsync(ulong deliveryTag, bool requeue = false)
        {
            if (_channel == null)
            {
                throw new InvalidOperationException("RabbitMQ is not connected");
            }

            _channel.BasicNack(deliveryTag, false, requeue);
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

