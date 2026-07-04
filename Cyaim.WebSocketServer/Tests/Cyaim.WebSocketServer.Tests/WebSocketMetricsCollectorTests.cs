using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    public class WebSocketMetricsCollectorTests
    {
        private sealed class Measurement
        {
            public string InstrumentName { get; init; }
            public long Value { get; init; }
            public Dictionary<string, object> Tags { get; init; }
        }

        /// <summary>
        /// Runs <paramref name="record"/> against a fresh collector while listening to the
        /// "Cyaim.WebSocketServer" meter, returning captured measurements.
        /// Other tests may create collectors with the same meter name concurrently,
        /// so assertions filter with unique tag values.
        /// </summary>
        private static List<Measurement> Capture(Action<WebSocketMetricsCollector> record)
        {
            var measurements = new ConcurrentBag<Measurement>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Cyaim.WebSocketServer")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            {
                var tagDict = new Dictionary<string, object>();
                foreach (var tag in tags)
                {
                    tagDict[tag.Key] = tag.Value;
                }
                measurements.Add(new Measurement { InstrumentName = instrument.Name, Value = value, Tags = tagDict });
            });
            listener.Start();

            var collector = new WebSocketMetricsCollector(NullLogger<WebSocketMetricsCollector>.Instance);
            try
            {
                record(collector);
            }
            finally
            {
                collector.Dispose();
            }

            return measurements.ToList();
        }

        private static string NewNodeId() => "node-" + Guid.NewGuid().ToString("N");

        [Fact]
        public void Constructor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new WebSocketMetricsCollector(null));
        }

        [Fact]
        public void AllRecordMethods_DoNotThrow()
        {
            var collector = new WebSocketMetricsCollector(NullLogger<WebSocketMetricsCollector>.Instance);
            try
            {
                collector.RecordConnectionEstablished("n1", "/ws");
                collector.RecordConnectionEstablished();
                collector.RecordConnectionClosed("n1", "/ws", "NormalClosure");
                collector.RecordConnectionClosed();
                collector.RecordMessageReceived(10, "n1", "/ws");
                collector.RecordMessageReceived(10);
                collector.RecordMessageSent(20, "n1", "/ws");
                collector.RecordMessageSent(20);
                collector.RecordBytesReceived(30, "n1");
                collector.RecordBytesReceived(30);
                collector.RecordBytesSent(40, "n1");
                collector.RecordBytesSent(40);
                collector.RecordClusterMessageForwarded("a", "b");
                collector.RecordClusterMessageForwarded(null, null);
                collector.RecordClusterMessageReceived("a");
                collector.RecordClusterMessageReceived(null);
                collector.RecordClusterNodeConnected("a");
                collector.RecordClusterNodeConnected(null);
                collector.RecordClusterNodeDisconnected("a");
                collector.UpdateClusterConnectionCount(5, "a");
                collector.UpdateClusterConnectionCount(-5);
                collector.RecordError("boom", "n1");
                collector.RecordError(null);
            }
            finally
            {
                collector.Dispose();
            }
        }

        [Fact]
        public void RecordConnectionEstablished_EmitsCountersWithNodeIdAndEndpointTags()
        {
            string nodeId = NewNodeId();
            var measurements = Capture(c => c.RecordConnectionEstablished(nodeId, "/ws"));

            var mine = measurements.Where(m => m.Tags.TryGetValue("node_id", out var v) && (string)v == nodeId).ToList();

            var total = Assert.Single(mine, m => m.InstrumentName == "websocket_connections_total");
            Assert.Equal(1, total.Value);
            Assert.Equal("/ws", total.Tags["endpoint"]);

            var active = Assert.Single(mine, m => m.InstrumentName == "websocket_connections_active");
            Assert.Equal(1, active.Value);
        }

        [Fact]
        public void RecordConnectionClosed_EmitsCloseStatusTag_AndDecrementsActive()
        {
            string nodeId = NewNodeId();
            var measurements = Capture(c => c.RecordConnectionClosed(nodeId, "/ws", "NormalClosure"));

            var mine = measurements.Where(m => m.Tags.TryGetValue("node_id", out var v) && (string)v == nodeId).ToList();

            var closed = Assert.Single(mine, m => m.InstrumentName == "websocket_connections_closed_total");
            Assert.Equal(1, closed.Value);
            Assert.Equal("NormalClosure", closed.Tags["close_status"]);
            Assert.Equal("/ws", closed.Tags["endpoint"]);

            var active = Assert.Single(mine, m => m.InstrumentName == "websocket_connections_active");
            Assert.Equal(-1, active.Value);
        }

        [Fact]
        public void RecordMessageReceived_EmitsCountHistogramAndBytes()
        {
            string nodeId = NewNodeId();
            var measurements = Capture(c => c.RecordMessageReceived(123, nodeId, "/chat"));

            var mine = measurements.Where(m => m.Tags.TryGetValue("node_id", out var v) && (string)v == nodeId).ToList();

            Assert.Equal(1, Assert.Single(mine, m => m.InstrumentName == "websocket_messages_received_total").Value);
            Assert.Equal(123, Assert.Single(mine, m => m.InstrumentName == "websocket_message_size_received_bytes").Value);
            Assert.Equal(123, Assert.Single(mine, m => m.InstrumentName == "websocket_bytes_received_total").Value);
            Assert.All(mine, m => Assert.Equal("/chat", m.Tags["endpoint"]));
        }

        [Fact]
        public void RecordMessageSent_EmitsCountHistogramAndBytes()
        {
            string nodeId = NewNodeId();
            var measurements = Capture(c => c.RecordMessageSent(456, nodeId, "/chat"));

            var mine = measurements.Where(m => m.Tags.TryGetValue("node_id", out var v) && (string)v == nodeId).ToList();

            Assert.Equal(1, Assert.Single(mine, m => m.InstrumentName == "websocket_messages_sent_total").Value);
            Assert.Equal(456, Assert.Single(mine, m => m.InstrumentName == "websocket_message_size_sent_bytes").Value);
            Assert.Equal(456, Assert.Single(mine, m => m.InstrumentName == "websocket_bytes_sent_total").Value);
        }

        [Fact]
        public void RecordError_EmitsErrorTypeAndNodeIdTags()
        {
            string nodeId = NewNodeId();
            var measurements = Capture(c => c.RecordError("handshake_failed", nodeId));

            var error = Assert.Single(measurements, m =>
                m.InstrumentName == "websocket_errors_total" &&
                m.Tags.TryGetValue("node_id", out var v) && (string)v == nodeId);
            Assert.Equal(1, error.Value);
            Assert.Equal("handshake_failed", error.Tags["error_type"]);
        }

        [Fact]
        public void RecordError_NullType_TaggedUnknown()
        {
            // No node_id filter possible here; use a dedicated capture and assert at least one
            // "unknown" error emitted by this collector instance.
            var measurements = Capture(c => c.RecordError(null));

            Assert.Contains(measurements, m =>
                m.InstrumentName == "websocket_errors_total" &&
                m.Tags.TryGetValue("error_type", out var v) && (string)v == "unknown");
        }

        [Fact]
        public void ClusterMetrics_UseSourceAndTargetNodeTags()
        {
            string source = NewNodeId();
            string target = NewNodeId();
            var measurements = Capture(c =>
            {
                c.RecordClusterMessageForwarded(source, target);
                c.RecordClusterMessageReceived(source);
                c.RecordClusterNodeConnected(source);
                c.RecordClusterNodeDisconnected(source);
                c.UpdateClusterConnectionCount(7, source);
            });

            var forwarded = Assert.Single(measurements, m =>
                m.InstrumentName == "websocket_cluster_messages_forwarded_total" &&
                m.Tags.TryGetValue("source_node_id", out var v) && (string)v == source);
            Assert.Equal(target, forwarded.Tags["target_node_id"]);

            Assert.Contains(measurements, m =>
                m.InstrumentName == "websocket_cluster_messages_received_total" &&
                m.Tags.TryGetValue("source_node_id", out var v) && (string)v == source);

            var nodeEvents = measurements.Where(m =>
                m.InstrumentName == "websocket_cluster_nodes_connected" &&
                m.Tags.TryGetValue("node_id", out var v) && (string)v == source).ToList();
            Assert.Equal(2, nodeEvents.Count);
            Assert.Contains(nodeEvents, m => m.Value == 1);
            Assert.Contains(nodeEvents, m => m.Value == -1);

            Assert.Contains(measurements, m =>
                m.InstrumentName == "websocket_cluster_connections_total" &&
                m.Value == 7 &&
                m.Tags.TryGetValue("node_id", out var v) && (string)v == source);
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var collector = new WebSocketMetricsCollector(NullLogger<WebSocketMetricsCollector>.Instance);
            collector.Dispose();
            collector.Dispose();
        }
    }
}
