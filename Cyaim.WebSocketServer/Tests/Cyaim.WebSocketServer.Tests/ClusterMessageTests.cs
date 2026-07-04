using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Cluster;

namespace Cyaim.WebSocketServer.Tests
{
    public class ClusterMessageTests
    {
        [Fact]
        public void ClusterMessage_Constructor_AssignsMessageIdAndTimestamp()
        {
            var before = DateTime.UtcNow;
            var message = new ClusterMessage();
            var after = DateTime.UtcNow;

            Assert.False(string.IsNullOrEmpty(message.MessageId));
            Assert.True(Guid.TryParse(message.MessageId, out _));
            Assert.InRange(message.Timestamp, before, after);
        }

        [Fact]
        public void ClusterMessage_JsonRoundTrip_PreservesAllFields()
        {
            var message = new ClusterMessage
            {
                Type = ClusterMessageType.ForwardWebSocketMessage,
                FromNodeId = "node-a",
                ToNodeId = "node-b",
                MessageId = "msg-123",
                Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                Payload = "{\"inner\":true}"
            };

            string json = JsonSerializer.Serialize(message);
            var back = JsonSerializer.Deserialize<ClusterMessage>(json);

            Assert.Equal(ClusterMessageType.ForwardWebSocketMessage, back.Type);
            Assert.Equal("node-a", back.FromNodeId);
            Assert.Equal("node-b", back.ToNodeId);
            Assert.Equal("msg-123", back.MessageId);
            Assert.Equal(message.Timestamp, back.Timestamp);
            Assert.Equal("{\"inner\":true}", back.Payload);
        }

        [Fact]
        public void ClusterMessage_Serialization_UsesJsonPropertyNames()
        {
            var message = new ClusterMessage
            {
                Type = ClusterMessageType.Heartbeat,
                FromNodeId = "n1",
                ToNodeId = "n2",
                MessageId = "id",
                Payload = "p"
            };

            string json = JsonSerializer.Serialize(message);

            Assert.Contains("\"type\":", json);
            Assert.Contains("\"from\":\"n1\"", json);
            Assert.Contains("\"to\":\"n2\"", json);
            Assert.Contains("\"id\":\"id\"", json);
            Assert.Contains("\"timestamp\":", json);
            Assert.Contains("\"payload\":\"p\"", json);
            // Enum serialized as its numeric value by default
            Assert.Contains("\"type\":4", json);
        }

        [Fact]
        public void ClusterMessage_NullToNodeId_MeansBroadcast_RoundTrips()
        {
            var message = new ClusterMessage { Type = ClusterMessageType.NodeJoin, FromNodeId = "n1", ToNodeId = null };

            var back = JsonSerializer.Deserialize<ClusterMessage>(JsonSerializer.Serialize(message));

            Assert.Null(back.ToNodeId);
        }

        [Fact]
        public void ForwardWebSocketMessage_ByteArrayData_RoundTripsAsBase64()
        {
            var data = new byte[] { 0, 1, 2, 250, 251, 252, 253, 254, 255 };
            var forward = new ForwardWebSocketMessage
            {
                ConnectionId = "conn-1",
                TargetNodeId = "node-b",
                Data = data,
                MessageType = 2,
                Endpoint = "/ws"
            };

            string json = JsonSerializer.Serialize(forward);
            Assert.Contains(Convert.ToBase64String(data), json);

            var back = JsonSerializer.Deserialize<ForwardWebSocketMessage>(json);
            Assert.Equal(data, back.Data);
            Assert.Equal("conn-1", back.ConnectionId);
            Assert.Equal("node-b", back.TargetNodeId);
            Assert.Equal(2, back.MessageType);
            Assert.Equal("/ws", back.Endpoint);
        }

        [Fact]
        public void ForwardWebSocketStream_RoundTrip_PreservesChunkFields()
        {
            var stream = new ForwardWebSocketStream
            {
                ConnectionId = "c",
                TargetNodeId = "t",
                StreamId = "s",
                ChunkIndex = 3,
                IsLastChunk = true,
                Data = new byte[] { 1, 2, 3 },
                MessageType = 1,
                TotalSize = 4096,
                Endpoint = "/ws"
            };

            var back = JsonSerializer.Deserialize<ForwardWebSocketStream>(JsonSerializer.Serialize(stream));

            Assert.Equal(3, back.ChunkIndex);
            Assert.True(back.IsLastChunk);
            Assert.Equal(new byte[] { 1, 2, 3 }, back.Data);
            Assert.Equal(4096, back.TotalSize);
        }

        [Fact]
        public void ClusterMessageType_NumericValues_AreStable()
        {
            // Wire-format stability: messages carry the numeric enum value, so reordering
            // the enum members would silently break cross-node/cross-version communication.
            // If this test fails, treat it as a breaking protocol change, not a test to "fix".
            Assert.Equal(0, (int)ClusterMessageType.RequestVote);
            Assert.Equal(1, (int)ClusterMessageType.RequestVoteResponse);
            Assert.Equal(2, (int)ClusterMessageType.AppendEntries);
            Assert.Equal(3, (int)ClusterMessageType.AppendEntriesResponse);
            Assert.Equal(4, (int)ClusterMessageType.Heartbeat);
            Assert.Equal(5, (int)ClusterMessageType.ForwardWebSocketMessage);
            Assert.Equal(6, (int)ClusterMessageType.ForwardWebSocketStream);
            Assert.Equal(7, (int)ClusterMessageType.ForwardWebSocketResponse);
            Assert.Equal(8, (int)ClusterMessageType.RegisterWebSocketConnection);
            Assert.Equal(9, (int)ClusterMessageType.UnregisterWebSocketConnection);
            Assert.Equal(10, (int)ClusterMessageType.QueryWebSocketConnection);
            Assert.Equal(11, (int)ClusterMessageType.NodeJoin);
            Assert.Equal(12, (int)ClusterMessageType.NodeLeave);
            Assert.Equal(13, (int)ClusterMessageType.NodeInfo);
            Assert.Equal(14, (int)ClusterMessageType.ClusterStateSync);
        }

        [Fact]
        public void ClusterMessageType_MemberCount_IsStable()
        {
            Assert.Equal(15, Enum.GetValues<ClusterMessageType>().Length);
        }
    }
}
