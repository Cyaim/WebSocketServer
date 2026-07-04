using Cyaim.WebSocketServer.Infrastructure.Configures;

namespace Cyaim.WebSocketServer.Tests
{
    public class WebSocketRouteOptionTests
    {
        [Fact]
        public void Defaults_ConnectionAndRequestLimits()
        {
            var option = new WebSocketRouteOption();

            Assert.Null(option.MaxConnectionLimit);
            Assert.Null(option.MaxRequestReceiveDataLimit);
            Assert.Null(option.MaxConnectionParallelForwardLimit);
            Assert.Null(option.MaxEndPointParallelForwardLimit);
        }

        [Fact]
        public void Defaults_RequireRequestId_IsTrue()
        {
            Assert.True(new WebSocketRouteOption().RequireRequestId);
        }

        [Fact]
        public void Defaults_AllowSameConnectionIdAccess_IsTrue()
        {
            Assert.True(new WebSocketRouteOption().AllowSameConnectionIdAccess);
        }

        [Fact]
        public void Defaults_ForwardTaskSyncProcessing_IsDisabled()
        {
            Assert.False(new WebSocketRouteOption().EnableForwardTaskSyncProcessingMode);
        }

        [Fact]
        public void Defaults_InjectionPropertyNames()
        {
            var option = new WebSocketRouteOption();

            Assert.Equal("WebSocketHttpContext", option.InjectionHttpContextPropertyName);
            Assert.Equal("WebSocketClient", option.InjectionWebSocketClientPropertyName);
        }

        [Fact]
        public void Defaults_UnconfiguredMembers_AreNull()
        {
            var option = new WebSocketRouteOption();

            Assert.Null(option.WebSocketChannels);
            Assert.Null(option.WatchAssemblyContext);
            Assert.Null(option.WatchAssemblyPath);
            Assert.Null(option.WatchAssemblyNamespacePrefix);
            Assert.Null(option.BandwidthLimitPolicy);
            Assert.Null(option.ApplicationServiceCollection);
        }

        [Fact]
        public void Defaults_JsonSerializerOptions_CaseInsensitiveNotIndented()
        {
            var option = new WebSocketRouteOption();

            Assert.NotNull(option.DefaultRequestJsonSerializerOptions);
            Assert.True(option.DefaultRequestJsonSerializerOptions.PropertyNameCaseInsensitive);
            Assert.False(option.DefaultRequestJsonSerializerOptions.WriteIndented);

            Assert.NotNull(option.DefaultResponseJsonSerializerOptions);
            Assert.True(option.DefaultResponseJsonSerializerOptions.PropertyNameCaseInsensitive);
            Assert.False(option.DefaultResponseJsonSerializerOptions.WriteIndented);
        }

        [Fact]
        public async Task OnBeforeConnection_NoSubscribers_ReturnsTrue()
        {
            var option = new WebSocketRouteOption();

            Assert.True(await option.OnBeforeConnection(null, option, "/ws", null));
        }

        [Fact]
        public async Task OnBeforeConnection_Subscriber_ControlsResult()
        {
            var option = new WebSocketRouteOption();
            option.BeforeConnectionEvent += (context, o, channel, logger) => Task.FromResult(false);

            Assert.False(await option.OnBeforeConnection(null, option, "/ws", null));
        }

        [Fact]
        public async Task OnDisconnected_Subscriber_IsInvoked()
        {
            var option = new WebSocketRouteOption();
            bool called = false;
            option.DisconnectedEvent += (context, o, channel, logger) => { called = true; return Task.CompletedTask; };

            await option.OnDisconnected(null, option, "/ws", null);

            Assert.True(called);
        }

        [Fact]
        public async Task OnException_NoSubscribers_ReturnsProvidedResponse()
        {
            var option = new WebSocketRouteOption();
            var response = new Infrastructure.Handlers.MvcHandler.MvcResponseScheme { Status = 1 };

            var result = await option.OnException(new InvalidOperationException(), null, response, null, option, "/ws", null);

            Assert.Same(response, result);
        }

        [Fact]
        public void BandwidthLimitPolicy_Defaults()
        {
            var policy = new BandwidthLimitPolicy();

            Assert.False(policy.Enabled);
            Assert.NotNull(policy.GlobalChannelBandwidthLimit);
            Assert.Empty(policy.GlobalChannelBandwidthLimit);
            Assert.NotNull(policy.ChannelMinBandwidthGuarantee);
            Assert.NotNull(policy.ChannelMaxBandwidthLimit);
            Assert.NotNull(policy.ChannelEnableAverageBandwidth);
            Assert.NotNull(policy.ChannelConnectionMinBandwidthGuarantee);
            Assert.NotNull(policy.ChannelConnectionMaxBandwidthLimit);
            Assert.NotNull(policy.EndPointMaxBandwidthLimit);
            Assert.NotNull(policy.EndPointMinBandwidthGuarantee);
        }
    }
}
