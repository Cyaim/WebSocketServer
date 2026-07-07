using System.Net.WebSockets;
using Cyaim.WebSocketServer.Dashboard.Controllers;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Dashboard.Services;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

namespace Cyaim.WebSocketServer.Dashboard.Tests
{
    [Collection("DashboardStatic")]
    public class StatisticsServiceTests
    {
        [Fact]
        public void RecordBytes_UpdatesPerConnectionStats()
        {
            using var svc = NewSvc();
            svc.RecordBytesReceived("c1", 100);
            svc.RecordBytesReceived("c1", 50);
            svc.RecordBytesSent("c1", 30);

            var s = svc.GetConnectionStats("c1");
            Assert.NotNull(s);
            Assert.Equal(150UL, s.BytesReceived);
            Assert.Equal(2UL, s.MessagesReceived);
            Assert.Equal(30UL, s.BytesSent);
            Assert.Equal(1UL, s.MessagesSent);
        }

        [Fact]
        public void RecordBytes_IgnoresEmptyIdOrNonPositive()
        {
            using var svc = NewSvc();
            svc.RecordBytesSent("", 100);
            svc.RecordBytesSent(null, 100);
            svc.RecordBytesSent("c1", 0);
            svc.RecordBytesSent("c1", -5);
            Assert.Null(svc.GetConnectionStats("c1"));
        }

        [Fact]
        public void RemoveConnection_DropsStats()
        {
            using var svc = NewSvc();
            svc.RecordBytesSent("c1", 10);
            Assert.NotNull(svc.GetConnectionStats("c1"));
            svc.RemoveConnection("c1");
            Assert.Null(svc.GetConnectionStats("c1"));
        }

        [Fact]
        public async Task Timer_AggregatesBandwidth_AndAppendsHistory()
        {
            T.ResetStandalone();
            using var svc = NewSvc();
            svc.RecordBytesReceived("c1", 500);
            svc.RecordBytesReceived("c2", 500);
            await Task.Delay(1300); // let the 1s timer fire at least once

            var bw = svc.GetBandwidthStatistics();
            Assert.Equal(1000UL, bw.TotalBytesReceived);
            Assert.Equal(2UL, bw.TotalMessagesReceived);

            var hist = svc.GetHistory();
            Assert.NotEmpty(hist);
            Assert.All(hist, s => Assert.True(s.Timestamp != default));
        }

        [Fact]
        public void RecordBytes_AppendsDataFlowEvents_WithDirectionAndSize()
        {
            using var svc = NewSvc();
            svc.RecordBytesReceived("c1", 100);
            svc.RecordBytesSent("c1", 30);

            var flow = svc.GetRecentMessages();
            Assert.Equal(2, flow.Count);
            Assert.Equal("Inbound", flow[0].Direction);
            Assert.Equal(100, flow[0].Size);
            Assert.Equal("Outbound", flow[1].Direction);
            Assert.Equal(30, flow[1].Size);
            Assert.All(flow, m => Assert.Equal("c1", m.ConnectionId));
            Assert.All(flow, m => Assert.False(string.IsNullOrEmpty(m.MessageId)));
        }

        [Fact]
        public void GetRecentMessages_SinceId_ReturnsOnlyNewer_AndHonorsMax()
        {
            using var svc = NewSvc();
            for (int i = 0; i < 10; i++)
            {
                svc.RecordBytesReceived("c1", 1 + i);
            }

            var all = svc.GetRecentMessages();
            Assert.Equal(10, all.Count);

            // incremental poll: only events after the 6th / 增量：只取第 6 条之后
            long since = long.Parse(all[5].MessageId);
            var newer = svc.GetRecentMessages(since);
            Assert.Equal(4, newer.Count);
            Assert.All(newer, m => Assert.True(long.Parse(m.MessageId) > since));

            // max keeps the NEWEST entries / max 保留最新的
            var capped = svc.GetRecentMessages(0, 3);
            Assert.Equal(3, capped.Count);
            Assert.Equal(all[^1].MessageId, capped[^1].MessageId);
        }

        [Fact]
        public void DataFlowBuffer_IsBounded()
        {
            using var svc = NewSvc();
            for (int i = 0; i < 700; i++)
            {
                svc.RecordBytesReceived("c1", 1);
            }
            // Capacity is 500; the buffer must not grow unbounded. 容量 500，不允许无界增长。
            Assert.True(svc.GetRecentMessages(0, int.MaxValue).Count <= 500);
        }

        private static DashboardStatisticsService NewSvc() => new DashboardStatisticsService(T.Log<DashboardStatisticsService>());
    }

    [Collection("DashboardStatic")]
    public class HelperServiceTests
    {
        [Fact]
        public void GetAllClusterConnections_IncludesLocal_InStandalone()
        {
            T.ResetStandalone();
            T.Seed("a", "b", "c");
            var svc = new DashboardHelperService();

            var conns = svc.GetAllClusterConnections();
            Assert.Equal(3, conns.Count);
            Assert.All(conns.Values, n => Assert.Equal("standalone", n));
        }

        [Fact]
        public void GetNodeStatusList_Standalone_IsConnectedHealthyNode()
        {
            T.ResetStandalone();
            T.Seed("a", "b");
            var svc = new DashboardHelperService();

            var nodes = svc.GetNodeStatusList();
            var n = Assert.Single(nodes);
            Assert.Equal("standalone", n.NodeId);
            Assert.True(n.IsConnected);
            Assert.True(n.IsLeader);
            Assert.Equal(2, n.ConnectionCount);
        }
    }

    [Collection("DashboardStatic")]
    public class HealthControllerTests
    {
        [Fact]
        public void Health_Standalone_IsHealthy_WithLocalCount()
        {
            T.ResetStandalone();
            T.Seed("a", "b", "c");
            var c = new HealthController(T.Log<HealthController>(), new DashboardHelperService());

            var resp = T.Unwrap(c.GetClusterHealth());
            Assert.True(resp.Success);
            Assert.True(resp.Data.IsHealthy);
            Assert.Equal(1, resp.Data.TotalNodes);
            Assert.Equal(1, resp.Data.HealthyNodes);
            Assert.True(resp.Data.HasLeader);
            Assert.Equal("standalone", resp.Data.Details["Mode"]);
            Assert.Equal(3, System.Convert.ToInt32(resp.Data.Details["LocalConnections"]));
        }
    }

    [Collection("DashboardStatic")]
    public class ClientControllerTests
    {
        private static ClientController New() =>
            new ClientController(T.Log<ClientController>(), new DashboardStatisticsService(T.Log<DashboardStatisticsService>()), new DashboardHelperService());

        [Fact]
        public void Count_Standalone_UsesLocalConnections()
        {
            T.ResetStandalone();
            T.Seed("a", "b");
            var resp = T.Unwrap(New().GetCount());
            Assert.True(resp.Success);
            Assert.Equal(2, resp.Data.Local);
            Assert.Equal(2, resp.Data.Total);
        }

        [Fact]
        public void GetAll_Standalone_ListsLocalConnections()
        {
            T.ResetStandalone();
            T.Seed("a", "b");
            var resp = T.Unwrap(New().GetAll(null));
            Assert.True(resp.Success);
            Assert.Equal(2, resp.Data.Count);
            Assert.Contains(resp.Data, x => x.ConnectionId == "a");
            Assert.All(resp.Data, x => Assert.Equal("Open", x.State));
        }

        [Fact]
        public async Task Disconnect_ClosesLocalSocket()
        {
            T.ResetStandalone();
            var seeded = T.Seed("kill-me");
            var resp = T.Unwrap(await New().Disconnect("kill-me"));
            Assert.True(resp.Success);
            Assert.True(seeded["kill-me"].Closed);
        }

        [Fact]
        public async Task Disconnect_UnknownConnection_NotFound()
        {
            T.ResetStandalone();
            var resp = T.Unwrap(await New().Disconnect("nope"));
            Assert.False(resp.Success);
        }
    }

    [Collection("DashboardStatic")]
    public class RouteControllerTests
    {
        [Fact]
        public void Routes_Standalone_MapsLocalConnectionsToCurrentNode()
        {
            T.ResetStandalone();
            T.Seed("x", "y");
            var c = new RouteController(T.Log<RouteController>());
            var resp = T.Unwrap(c.GetAll());
            Assert.True(resp.Success);
            Assert.Equal(2, resp.Data.Count);
            Assert.Equal("standalone", resp.Data["x"]);
        }

        [Fact]
        public void RoutesByNode_Standalone_ReturnsLocalConnections()
        {
            T.ResetStandalone();
            T.Seed("x", "y", "z");
            var c = new RouteController(T.Log<RouteController>());
            var resp = T.Unwrap(c.GetByNode("standalone"));
            Assert.True(resp.Success);
            Assert.Equal(3, resp.Data.Count);
        }
    }

    [Collection("DashboardStatic")]
    public class StatisticsControllerTests
    {
        private static StatisticsController New(DashboardStatisticsService s) =>
            new StatisticsController(T.Log<StatisticsController>(), s, new DashboardHelperService());

        [Fact]
        public void Bandwidth_ReturnsStats()
        {
            T.ResetStandalone();
            using var svc = new DashboardStatisticsService(T.Log<DashboardStatisticsService>());
            var resp = T.Unwrap(New(svc).GetBandwidth());
            Assert.True(resp.Success);
            Assert.NotNull(resp.Data);
        }

        [Fact]
        public async Task TimeSeries_ReturnsSamplesAfterTimer()
        {
            T.ResetStandalone();
            using var svc = new DashboardStatisticsService(T.Log<DashboardStatisticsService>());
            svc.RecordBytesReceived("c1", 200);
            await Task.Delay(1300);
            var resp = T.Unwrap(New(svc).GetTimeSeries());
            Assert.True(resp.Success);
            Assert.NotEmpty(resp.Data);
        }
    }

    [Collection("DashboardStatic")]
    public class ClusterControllerTests
    {
        [Fact]
        public void Overview_Standalone_UsesLocalCounts()
        {
            T.ResetStandalone();
            T.Seed("a", "b", "c", "d");
            var c = new ClusterController(T.Log<ClusterController>(), new DashboardHelperService());
            var resp = T.Unwrap(c.GetOverview());
            Assert.True(resp.Success);
            Assert.Equal(1, resp.Data.TotalNodes);
            Assert.Equal(4, resp.Data.TotalConnections);
            Assert.Equal(4, resp.Data.LocalConnections);
        }

        [Fact]
        public void Nodes_Standalone_ReturnsSelfNode()
        {
            T.ResetStandalone();
            var c = new ClusterController(T.Log<ClusterController>(), new DashboardHelperService());
            var resp = T.Unwrap(c.GetNodes());
            Assert.True(resp.Success);
            var n = Assert.Single(resp.Data);
            Assert.Equal("standalone", n.NodeId);
        }
    }

    [Collection("DashboardStatic")]
    public class MessageControllerTests
    {
        [Fact]
        public void GetRecent_ReturnsRecordedFlow_AndSupportsIncrementalPoll()
        {
            T.ResetStandalone();
            using var svc = new DashboardStatisticsService(T.Log<DashboardStatisticsService>());
            var c = new MessageController(T.Log<MessageController>(), svc, new DashboardHelperService());

            svc.RecordBytesReceived("c1", 64);
            svc.RecordBytesSent("c1", 16);

            var resp = T.Unwrap(c.GetRecent());
            Assert.True(resp.Success);
            Assert.Equal(2, resp.Data.Count);
            Assert.Equal("Inbound", resp.Data[0].Direction);
            Assert.Equal("Outbound", resp.Data[1].Direction);

            // Incremental poll from the last seen id returns nothing new. 从最后一条增量拉取应为空。
            long last = long.Parse(resp.Data[^1].MessageId);
            var next = T.Unwrap(c.GetRecent(last));
            Assert.True(next.Success);
            Assert.Empty(next.Data);
        }

        [Fact]
        public async Task Broadcast_CountsSuccessfulSends()
        {
            // Regression: the count must unwrap Ok()-wrapped ActionResult values (Value is null there),
            // otherwise it always reports 0 even though every client received the message.
            // 回归：Ok() 包装的 ActionResult 其 .Value 为 null，计数必须从 .Result 解包，否则恒为 0。
            T.ResetStandalone();
            T.Seed("b1", "b2", "b3");
            using var svc = new DashboardStatisticsService(T.Log<DashboardStatisticsService>());
            var c = new MessageController(T.Log<MessageController>(), svc, new DashboardHelperService());

            var resp = T.Unwrap(await c.Broadcast(new BroadcastMessageRequest { Content = "hi", MessageType = "Text" }));
            Assert.True(resp.Success);
            Assert.Equal(3, resp.Data);
        }
    }
}
