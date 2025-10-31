namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    /// <summary>
    /// Cluster configure
    /// </summary>
    public class ClusterOption
    {
        /// <summary>
        /// Cluster channel name
        /// </summary>
        public string ChannelName { get; set; } = "/cluster";

        /// <summary>
        /// Node connection link
        /// </summary>
        public string[] Nodes { get; set; }

        /// <summary>
        /// Service level
        /// </summary>
        public ServiceLevel NodeLevel { get; set; }

        /// <summary>
        /// Instruct the current node enable services.
        /// If set false current node will forward request to other node.
        /// Only NodeLevel are valid for Master.
        /// </summary>
        public bool IsEnableLoadBalance { get; set; }

        /// <summary>
        /// Transport type: ws, redis, rabbitmq
        /// </summary>
        public string TransportType { get; set; } = "ws";

        /// <summary>
        /// Current node ID (auto-generated if not set)
        /// </summary>
        public string NodeId { get; set; }

        /// <summary>
        /// Current node address (for WebSocket transport)
        /// </summary>
        public string NodeAddress { get; set; }

        /// <summary>
        /// Current node port (for WebSocket transport)
        /// </summary>
        public int NodePort { get; set; }

        /// <summary>
        /// Redis connection string (for Redis transport)
        /// </summary>
        public string RedisConnectionString { get; set; }

        /// <summary>
        /// RabbitMQ connection string (for RabbitMQ transport)
        /// </summary>
        public string RabbitMQConnectionString { get; set; }
    }

    /// <summary>
    /// Service node
    /// </summary>
    public enum ServiceLevel
    {
        /// <summary>
        /// Master node
        /// </summary>
        Master,

        /// <summary>
        /// Slave node
        /// </summary>
        Slave
    }
}