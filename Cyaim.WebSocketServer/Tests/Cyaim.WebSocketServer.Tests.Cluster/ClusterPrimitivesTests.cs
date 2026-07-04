using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Cluster.Transports;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    [Collection("ClusterStaticState")]
    public class ClusterNodeTests
    {
        [Fact]
        public void Protocol_DefaultsToWs()
        {
            Assert.Equal("ws", new ClusterNode().Protocol);
        }

        [Fact]
        public void FullAddress_CombinesProtocolAddressAndPort()
        {
            var node = new ClusterNode { Protocol = "wss", Address = "10.0.0.1", Port = 8443 };
            Assert.Equal("wss://10.0.0.1:8443", node.FullAddress);
        }

        [Fact]
        public void RaftNodeState_HasExpectedValues()
        {
            Assert.Equal(0, (int)RaftNodeState.Follower);
            Assert.Equal(1, (int)RaftNodeState.Candidate);
            Assert.Equal(2, (int)RaftNodeState.Leader);
        }
    }

    [Collection("ClusterStaticState")]
    public class ClusterMessageTests
    {
        [Fact]
        public void Constructor_AssignsUniqueMessageId_AndUtcTimestamp()
        {
            var before = DateTime.UtcNow.AddSeconds(-5);
            var m1 = new ClusterMessage();
            var m2 = new ClusterMessage();
            var after = DateTime.UtcNow.AddSeconds(5);

            Assert.True(Guid.TryParse(m1.MessageId, out _));
            Assert.NotEqual(m1.MessageId, m2.MessageId);
            Assert.InRange(m1.Timestamp, before, after);
        }

        [Fact]
        public void Serialization_UsesShortJsonPropertyNames()
        {
            var message = new ClusterMessage
            {
                Type = ClusterMessageType.Heartbeat,
                FromNodeId = "a",
                ToNodeId = "b",
                MessageId = "id-1",
                Payload = "p"
            };

            var json = JsonSerializer.Serialize(message);
            var element = JsonSerializer.Deserialize<JsonElement>(json);

            Assert.Equal((int)ClusterMessageType.Heartbeat, element.GetProperty("type").GetInt32());
            Assert.Equal("a", element.GetProperty("from").GetString());
            Assert.Equal("b", element.GetProperty("to").GetString());
            Assert.Equal("id-1", element.GetProperty("id").GetString());
            Assert.Equal("p", element.GetProperty("payload").GetString());
            Assert.True(element.TryGetProperty("timestamp", out _));
        }

        [Fact]
        public void Serialization_RoundTrips()
        {
            var message = new ClusterMessage
            {
                Type = ClusterMessageType.ForwardWebSocketMessage,
                FromNodeId = "nodeA",
                ToNodeId = "nodeB",
                Payload = "data"
            };

            var restored = JsonSerializer.Deserialize<ClusterMessage>(JsonSerializer.Serialize(message));

            Assert.Equal(message.Type, restored.Type);
            Assert.Equal(message.FromNodeId, restored.FromNodeId);
            Assert.Equal(message.ToNodeId, restored.ToNodeId);
            Assert.Equal(message.MessageId, restored.MessageId);
            Assert.Equal(message.Payload, restored.Payload);
        }

        [Fact]
        public void ForwardWebSocketMessage_RoundTrips()
        {
            var forward = new ForwardWebSocketMessage
            {
                ConnectionId = "c1",
                TargetNodeId = "nodeB",
                Data = Encoding.UTF8.GetBytes("hello"),
                MessageType = 1,
                Endpoint = "/ws"
            };

            var restored = JsonSerializer.Deserialize<ForwardWebSocketMessage>(JsonSerializer.Serialize(forward));

            Assert.Equal(forward.ConnectionId, restored.ConnectionId);
            Assert.Equal(forward.TargetNodeId, restored.TargetNodeId);
            Assert.Equal(forward.Data, restored.Data);
            Assert.Equal(forward.MessageType, restored.MessageType);
            Assert.Equal(forward.Endpoint, restored.Endpoint);
        }

        [Fact]
        public void ForwardWebSocketStream_RoundTrips()
        {
            var stream = new ForwardWebSocketStream
            {
                ConnectionId = "c1",
                TargetNodeId = "nodeB",
                StreamId = "s1",
                ChunkIndex = 2,
                IsLastChunk = true,
                Data = new byte[] { 1, 2, 3 },
                MessageType = 2,
                TotalSize = 1000,
                Endpoint = "/ws"
            };

            var restored = JsonSerializer.Deserialize<ForwardWebSocketStream>(JsonSerializer.Serialize(stream));

            Assert.Equal(stream.StreamId, restored.StreamId);
            Assert.Equal(stream.ChunkIndex, restored.ChunkIndex);
            Assert.True(restored.IsLastChunk);
            Assert.Equal(stream.Data, restored.Data);
            Assert.Equal(1000, restored.TotalSize);
        }

        [Fact]
        public void RaftMessages_RoundTrip()
        {
            var vote = new RequestVoteMessage { Term = 3, CandidateId = "n1", LastLogIndex = 7, LastLogTerm = 2 };
            var voteRestored = JsonSerializer.Deserialize<RequestVoteMessage>(JsonSerializer.Serialize(vote));
            Assert.Equal(3, voteRestored.Term);
            Assert.Equal("n1", voteRestored.CandidateId);
            Assert.Equal(7, voteRestored.LastLogIndex);
            Assert.Equal(2, voteRestored.LastLogTerm);

            var response = new RequestVoteResponseMessage { Term = 3, VoteGranted = true };
            var responseRestored = JsonSerializer.Deserialize<RequestVoteResponseMessage>(JsonSerializer.Serialize(response));
            Assert.True(responseRestored.VoteGranted);

            var append = new AppendEntriesMessage
            {
                Term = 4,
                LeaderId = "n1",
                PrevLogIndex = 1,
                PrevLogTerm = 2,
                Entries = new[] { new RaftLogEntry { Index = 2, Term = 4, Command = "cmd", Data = "d" } },
                LeaderCommit = 2
            };
            var appendRestored = JsonSerializer.Deserialize<AppendEntriesMessage>(JsonSerializer.Serialize(append));
            Assert.Equal("n1", appendRestored.LeaderId);
            Assert.Single(appendRestored.Entries);
            Assert.Equal("cmd", appendRestored.Entries[0].Command);

            var appendResponse = new AppendEntriesResponseMessage { Term = 4, Success = true, LastLogIndex = 2 };
            var appendResponseRestored = JsonSerializer.Deserialize<AppendEntriesResponseMessage>(JsonSerializer.Serialize(appendResponse));
            Assert.True(appendResponseRestored.Success);
            Assert.Equal(2, appendResponseRestored.LastLogIndex);
        }

        [Fact]
        public void WebSocketConnectionRegistration_RoundTrips()
        {
            var registration = new WebSocketConnectionRegistration
            {
                ConnectionId = "c1",
                NodeId = "nodeA",
                Endpoint = "/ws",
                RemoteIpAddress = "1.2.3.4",
                RemotePort = 5678,
                RegisteredAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            };

            var restored = JsonSerializer.Deserialize<WebSocketConnectionRegistration>(JsonSerializer.Serialize(registration));

            Assert.Equal(registration.ConnectionId, restored.ConnectionId);
            Assert.Equal(registration.NodeId, restored.NodeId);
            Assert.Equal(registration.Endpoint, restored.Endpoint);
            Assert.Equal(registration.RemoteIpAddress, restored.RemoteIpAddress);
            Assert.Equal(registration.RemotePort, restored.RemotePort);
            Assert.Equal(registration.RegisteredAt, restored.RegisteredAt);
        }
    }

    public class ClusterOptionTests
    {
        [Fact]
        public void RemainingProperties_AreSettableAndReadable()
        {
            // Covers the NodeLevel/IsEnableLoadBalance/NodeAddress/NodePort auto-property
            // setters+getters that no other test in the suite happens to touch.
            var option = new ClusterOption
            {
                NodeLevel = ServiceLevel.Slave,
                IsEnableLoadBalance = true,
                NodeAddress = "10.0.0.1",
                NodePort = 6001
            };

            Assert.Equal(ServiceLevel.Slave, option.NodeLevel);
            Assert.True(option.IsEnableLoadBalance);
            Assert.Equal("10.0.0.1", option.NodeAddress);
            Assert.Equal(6001, option.NodePort);
        }
    }

    [Collection("ClusterStaticState")]
    public class GlobalClusterCenterTests : StaticStateGuard
    {
        [Fact]
        public void Properties_AreSettableAndReadable()
        {
            var option = new ClusterOption { NodeId = "n1" };
            var provider = new DefaultWebSocketConnectionProvider();

            GlobalClusterCenter.ClusterContext = option;
            GlobalClusterCenter.ConnectionProvider = provider;

            Assert.Same(option, GlobalClusterCenter.ClusterContext);
            Assert.Same(provider, GlobalClusterCenter.ConnectionProvider);
            Assert.Null(GlobalClusterCenter.ClusterManager);
            Assert.Null(GlobalClusterCenter.StatisticsRecorder);
        }
    }

    [Collection("ClusterStaticState")]
    public class ClusterTransportFactoryTests
    {
        [Fact]
        public void CreateTransport_NullOption_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                ClusterTransportFactory.CreateTransport(NullLoggerFactory.Instance, "n1", null));
        }

        [Theory]
        [InlineData("ws")]
        [InlineData("wss")]
        [InlineData("websocket")]
        [InlineData("WS")]
        [InlineData(null)] // defaults to ws
        public void CreateTransport_WebSocketVariants_ReturnWebSocketClusterTransport(string transportType)
        {
            var option = new ClusterOption { TransportType = transportType };

            using var transport = ClusterTransportFactory.CreateTransport(NullLoggerFactory.Instance, "n1", option);

            Assert.IsType<WebSocketClusterTransport>(transport);
        }

        [Theory]
        [InlineData("redis")]
        [InlineData("rabbitmq")]
        [InlineData("kafka")]
        public void CreateTransport_UnsupportedTypes_ThrowNotSupported(string transportType)
        {
            var option = new ClusterOption { TransportType = transportType };

            Assert.Throws<NotSupportedException>(() =>
                ClusterTransportFactory.CreateTransport(NullLoggerFactory.Instance, "n1", option));
        }
    }

    [Collection("ClusterStaticState")]
    public class DefaultWebSocketConnectionProviderTests : StaticStateGuard
    {
        private readonly DefaultWebSocketConnectionProvider _provider = new DefaultWebSocketConnectionProvider();

        [Fact]
        public void GetConnection_NullOrEmptyOrUnknown_ReturnsNull()
        {
            Assert.Null(_provider.GetConnection(null));
            Assert.Null(_provider.GetConnection(""));
            Assert.Null(_provider.GetConnection("missing"));
        }

        [Fact]
        public void GetConnection_KnownId_ReturnsSocketFromMvcChannelHandler()
        {
            var socket = new TestWebSocket();
            MvcChannelHandler.Clients["c1"] = socket;

            Assert.Same(socket, _provider.GetConnection("c1"));
        }

        [Fact]
        public async Task SendAsync_OpenSocket_SendsAndReturnsTrue()
        {
            var socket = new TestWebSocket();
            MvcChannelHandler.Clients["c1"] = socket;
            var data = Encoding.UTF8.GetBytes("hi");

            var ok = await _provider.SendAsync("c1", data, WebSocketMessageType.Text);

            Assert.True(ok);
            var frame = Assert.Single(socket.Frames);
            Assert.Equal(data, frame.Payload);
            Assert.True(frame.EndOfMessage);
        }

        [Fact]
        public async Task SendAsync_MissingConnection_ReturnsFalse()
        {
            Assert.False(await _provider.SendAsync("missing", new byte[] { 1 }, WebSocketMessageType.Text));
        }

        [Fact]
        public async Task SendAsync_ClosedSocket_ReturnsFalse()
        {
            MvcChannelHandler.Clients["c1"] = new TestWebSocket(WebSocketState.Closed);

            Assert.False(await _provider.SendAsync("c1", new byte[] { 1 }, WebSocketMessageType.Text));
        }

        [Fact]
        public async Task SendAsync_SocketThrows_ReturnsFalse()
        {
            MvcChannelHandler.Clients["c1"] = new ThrowingWebSocket();

            Assert.False(await _provider.SendAsync("c1", new byte[] { 1 }, WebSocketMessageType.Text));
        }
    }
}
