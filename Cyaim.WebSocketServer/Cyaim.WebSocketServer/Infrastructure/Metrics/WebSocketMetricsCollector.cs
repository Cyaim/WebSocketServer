using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.Metrics
{
    /// <summary>
    /// WebSocket metrics collector for OpenTelemetry
    /// WebSocket 指标收集器，用于 OpenTelemetry
    /// </summary>
    public class WebSocketMetricsCollector
    {
        private readonly Meter _meter;
        private readonly ILogger<WebSocketMetricsCollector> _logger;

        // Connection metrics / 连接指标
        private readonly Counter<long> _connectionsTotal;
        private readonly Counter<long> _connectionsClosed;
        private readonly UpDownCounter<long> _connectionsActive;

        // Message metrics / 消息指标
        private readonly Counter<long> _messagesReceived;
        private readonly Counter<long> _messagesSent;
        private readonly Histogram<long> _messageSizeReceived;
        private readonly Histogram<long> _messageSizeSent;

        // Byte metrics / 字节指标
        private readonly Counter<long> _bytesReceived;
        private readonly Counter<long> _bytesSent;

        // Cluster metrics / 集群指标
        private readonly Counter<long> _clusterMessagesForwarded;
        private readonly Counter<long> _clusterMessagesReceived;
        private readonly UpDownCounter<long> _clusterNodesConnected;
        private readonly UpDownCounter<long> _clusterConnectionsTotal;

        // Error metrics / 错误指标
        private readonly Counter<long> _errorsTotal;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        public WebSocketMetricsCollector(ILogger<WebSocketMetricsCollector> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _meter = new Meter("Cyaim.WebSocketServer", "1.0.0");

            // Initialize connection metrics / 初始化连接指标
            _connectionsTotal = _meter.CreateCounter<long>(
                "websocket_connections_total",
                "count",
                "Total number of WebSocket connections established / WebSocket 连接总数");
            
            _connectionsClosed = _meter.CreateCounter<long>(
                "websocket_connections_closed_total",
                "count",
                "Total number of WebSocket connections closed / WebSocket 连接关闭总数");
            
            _connectionsActive = _meter.CreateUpDownCounter<long>(
                "websocket_connections_active",
                "count",
                "Current number of active WebSocket connections / 当前活跃的 WebSocket 连接数");

            // Initialize message metrics / 初始化消息指标
            _messagesReceived = _meter.CreateCounter<long>(
                "websocket_messages_received_total",
                "count",
                "Total number of messages received / 接收的消息总数");
            
            _messagesSent = _meter.CreateCounter<long>(
                "websocket_messages_sent_total",
                "count",
                "Total number of messages sent / 发送的消息总数");
            
            _messageSizeReceived = _meter.CreateHistogram<long>(
                "websocket_message_size_received_bytes",
                "bytes",
                "Size of received messages in bytes / 接收消息的大小（字节）");
            
            _messageSizeSent = _meter.CreateHistogram<long>(
                "websocket_message_size_sent_bytes",
                "bytes",
                "Size of sent messages in bytes / 发送消息的大小（字节）");

            // Initialize byte metrics / 初始化字节指标
            _bytesReceived = _meter.CreateCounter<long>(
                "websocket_bytes_received_total",
                "bytes",
                "Total bytes received / 接收的总字节数");
            
            _bytesSent = _meter.CreateCounter<long>(
                "websocket_bytes_sent_total",
                "bytes",
                "Total bytes sent / 发送的总字节数");

            // Initialize cluster metrics / 初始化集群指标
            _clusterMessagesForwarded = _meter.CreateCounter<long>(
                "websocket_cluster_messages_forwarded_total",
                "count",
                "Total number of messages forwarded in cluster / 集群中转发的消息总数");
            
            _clusterMessagesReceived = _meter.CreateCounter<long>(
                "websocket_cluster_messages_received_total",
                "count",
                "Total number of messages received from cluster / 从集群接收的消息总数");
            
            _clusterNodesConnected = _meter.CreateUpDownCounter<long>(
                "websocket_cluster_nodes_connected",
                "count",
                "Current number of connected cluster nodes / 当前连接的集群节点数");
            
            _clusterConnectionsTotal = _meter.CreateUpDownCounter<long>(
                "websocket_cluster_connections_total",
                "count",
                "Total number of connections in cluster / 集群中的连接总数");

            // Initialize error metrics / 初始化错误指标
            _errorsTotal = _meter.CreateCounter<long>(
                "websocket_errors_total",
                "count",
                "Total number of errors / 错误总数");
        }

        /// <summary>
        /// Record connection established / 记录连接建立
        /// </summary>
        /// <param name="nodeId">Node ID (optional) / 节点 ID（可选）</param>
        /// <param name="endpoint">Endpoint path (optional) / 端点路径（可选）</param>
        public void RecordConnectionEstablished(string nodeId = null, string endpoint = null)
        {
            var tags = new List<KeyValuePair<string, object>>();
            if (!string.IsNullOrEmpty(nodeId))
            {
                tags.Add(new KeyValuePair<string, object>("node_id", nodeId));
            }
            if (!string.IsNullOrEmpty(endpoint))
            {
                tags.Add(new KeyValuePair<string, object>("endpoint", endpoint));
            }

            _connectionsTotal.Add(1, tags.ToArray());
            _connectionsActive.Add(1, tags.ToArray());
        }

        /// <summary>
        /// Record connection closed / 记录连接关闭
        /// </summary>
        /// <param name="nodeId">Node ID (optional) / 节点 ID（可选）</param>
        /// <param name="endpoint">Endpoint path (optional) / 端点路径（可选）</param>
        /// <param name="closeStatus">Close status (optional) / 关闭状态（可选）</param>
        public void RecordConnectionClosed(string nodeId = null, string endpoint = null, string closeStatus = null)
        {
            var tags = new List<KeyValuePair<string, object>>();
            if (!string.IsNullOrEmpty(nodeId))
            {
                tags.Add(new KeyValuePair<string, object>("node_id", nodeId));
            }
            if (!string.IsNullOrEmpty(endpoint))
            {
                tags.Add(new KeyValuePair<string, object>("endpoint", endpoint));
            }
            if (!string.IsNullOrEmpty(closeStatus))
            {
                tags.Add(new KeyValuePair<string, object>("close_status", closeStatus));
            }

            _connectionsClosed.Add(1, tags.ToArray());
            _connectionsActive.Add(-1, tags.ToArray());
        }

        /// <summary>
        /// Record message received / 记录接收的消息
        /// </summary>
        /// <param name="size">Message size in bytes / 消息大小（字节）</param>
        /// <param name="nodeId">Node ID (optional) / 节点 ID（可选）</param>
        /// <param name="endpoint">Endpoint path (optional) / 端点路径（可选）</param>
        public void RecordMessageReceived(long size, string nodeId = null, string endpoint = null)
        {
            var tags = new List<KeyValuePair<string, object>>();
            if (!string.IsNullOrEmpty(nodeId))
            {
                tags.Add(new KeyValuePair<string, object>("node_id", nodeId));
            }
            if (!string.IsNullOrEmpty(endpoint))
            {
                tags.Add(new KeyValuePair<string, object>("endpoint", endpoint));
            }

            _messagesReceived.Add(1, tags.ToArray());
            _messageSizeReceived.Record(size, tags.ToArray());
            _bytesReceived.Add(size, tags.ToArray());
        }

        /// <summary>
        /// Record message sent / 记录发送的消息
        /// </summary>
        /// <param name="size">Message size in bytes / 消息大小（字节）</param>
        /// <param name="nodeId">Node ID (optional) / 节点 ID（可选）</param>
        /// <param name="endpoint">Endpoint path (optional) / 端点路径（可选）</param>
        public void RecordMessageSent(long size, string nodeId = null, string endpoint = null)
        {
            var tags = new List<KeyValuePair<string, object>>();
            if (!string.IsNullOrEmpty(nodeId))
            {
                tags.Add(new KeyValuePair<string, object>("node_id", nodeId));
            }
            if (!string.IsNullOrEmpty(endpoint))
            {
                tags.Add(new KeyValuePair<string, object>("endpoint", endpoint));
            }

            _messagesSent.Add(1, tags.ToArray());
            _messageSizeSent.Record(size, tags.ToArray());
            _bytesSent.Add(size, tags.ToArray());
        }

        /// <summary>
        /// Record bytes received / 记录接收的字节
        /// </summary>
        /// <param name="bytes">Number of bytes / 字节数</param>
        /// <param name="nodeId">Node ID (optional) / 节点 ID（可选）</param>
        public void RecordBytesReceived(long bytes, string nodeId = null)
        {
            var tags = new List<KeyValuePair<string, object>>();
            if (!string.IsNullOrEmpty(nodeId))
            {
                tags.Add(new KeyValuePair<string, object>("node_id", nodeId));
            }

            _bytesReceived.Add(bytes, tags.ToArray());
        }

        /// <summary>
        /// Record bytes sent / 记录发送的字节
        /// </summary>
        /// <param name="bytes">Number of bytes / 字节数</param>
        /// <param name="nodeId">Node ID (optional) / 节点 ID（可选）</param>
        public void RecordBytesSent(long bytes, string nodeId = null)
        {
            var tags = new List<KeyValuePair<string, object>>();
            if (!string.IsNullOrEmpty(nodeId))
            {
                tags.Add(new KeyValuePair<string, object>("node_id", nodeId));
            }

            _bytesSent.Add(bytes, tags.ToArray());
        }

        /// <summary>
        /// Record cluster message forwarded / 记录集群消息转发
        /// </summary>
        /// <param name="sourceNodeId">Source node ID / 源节点 ID</param>
        /// <param name="targetNodeId">Target node ID / 目标节点 ID</param>
        public void RecordClusterMessageForwarded(string sourceNodeId, string targetNodeId)
        {
            var tags = new[]
            {
                new KeyValuePair<string, object>("source_node_id", sourceNodeId ?? "unknown"),
                new KeyValuePair<string, object>("target_node_id", targetNodeId ?? "unknown")
            };

            _clusterMessagesForwarded.Add(1, tags);
        }

        /// <summary>
        /// Record cluster message received / 记录集群消息接收
        /// </summary>
        /// <param name="sourceNodeId">Source node ID / 源节点 ID</param>
        public void RecordClusterMessageReceived(string sourceNodeId)
        {
            var tags = new[]
            {
                new KeyValuePair<string, object>("source_node_id", sourceNodeId ?? "unknown")
            };

            _clusterMessagesReceived.Add(1, tags);
        }

        /// <summary>
        /// Record cluster node connected / 记录集群节点连接
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        public void RecordClusterNodeConnected(string nodeId)
        {
            var tags = new[]
            {
                new KeyValuePair<string, object>("node_id", nodeId ?? "unknown")
            };

            _clusterNodesConnected.Add(1, tags);
        }

        /// <summary>
        /// Record cluster node disconnected / 记录集群节点断开
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        public void RecordClusterNodeDisconnected(string nodeId)
        {
            var tags = new[]
            {
                new KeyValuePair<string, object>("node_id", nodeId ?? "unknown")
            };

            _clusterNodesConnected.Add(-1, tags);
        }

        /// <summary>
        /// Update cluster connection count / 更新集群连接数
        /// </summary>
        /// <param name="delta">Change in connection count / 连接数变化</param>
        /// <param name="nodeId">Node ID (optional) / 节点 ID（可选）</param>
        public void UpdateClusterConnectionCount(long delta, string nodeId = null)
        {
            var tags = new List<KeyValuePair<string, object>>();
            if (!string.IsNullOrEmpty(nodeId))
            {
                tags.Add(new KeyValuePair<string, object>("node_id", nodeId));
            }

            _clusterConnectionsTotal.Add(delta, tags.ToArray());
        }

        /// <summary>
        /// Record error / 记录错误
        /// </summary>
        /// <param name="errorType">Error type / 错误类型</param>
        /// <param name="nodeId">Node ID (optional) / 节点 ID（可选）</param>
        public void RecordError(string errorType, string nodeId = null)
        {
            var tags = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("error_type", errorType ?? "unknown")
            };
            if (!string.IsNullOrEmpty(nodeId))
            {
                tags.Add(new KeyValuePair<string, object>("node_id", nodeId));
            }

            _errorsTotal.Add(1, tags.ToArray());
        }

        /// <summary>
        /// Dispose resources / 释放资源
        /// </summary>
        public void Dispose()
        {
            _meter?.Dispose();
        }
    }
}
