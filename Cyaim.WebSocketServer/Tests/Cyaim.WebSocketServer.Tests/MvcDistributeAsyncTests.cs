using System.Text.Json.Nodes;
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Tests for the static MvcChannelHandler.MvcDistributeAsync endpoint dispatcher.
    /// Uses a hand-built WatchAssemblyContext pointing at MvcTestSupport.WsTestController.
    /// Touches WebSocketRouteOption.ApplicationServices, so it runs in the StaticState
    /// collection and restores the statics it changes.
    /// </summary>
    [Collection("StaticState")]
    public class MvcDistributeAsyncTests : IDisposable
    {
        private readonly IServiceProvider _previousServices;
        private readonly ServiceProvider _provider;
        private readonly MvcTestSupport.StubLifetime _lifetime = new MvcTestSupport.StubLifetime();

        public MvcDistributeAsyncTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHostApplicationLifetime>(_lifetime);
            services.AddSingleton<MvcTestSupport.IGreetService, MvcTestSupport.GreetService>();
            _provider = services.BuildServiceProvider();

            WebSocketRouteOption.ApplicationServices = _provider;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
            MvcTestSupport.ResetCachedScopeFactory();
            _provider.Dispose();
        }

        private static WebSocketRouteOption CreateOptions()
            => new WebSocketRouteOption
            {
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController))
            };

        private Task<MvcResponseScheme> DistributeAsync(WebSocketRouteOption options, string target, string bodyJson, string id = "req-1")
        {
            var request = new MvcRequestScheme { Id = id, Target = target };
            JsonObject body = bodyJson == null ? null : JsonNode.Parse(bodyJson).AsObject();
            return MvcChannelHandler.MvcDistributeAsync(
                options,
                new DefaultHttpContext(),
                new TestWebSocket(),
                request,
                body,
                NullLogger<WebSocketRouteMiddleware>.Instance,
                _lifetime);
        }

        #region Endpoint resolution

        [Fact]
        public async Task UnknownTarget_ReturnsStatus2()
        {
            var response = await DistributeAsync(CreateOptions(), "nothing.here", null, id: "abc");

            Assert.Equal(2, response.Status);
            Assert.Equal("abc", response.Id);
            Assert.Equal("nothing.here", response.Target);
            Assert.True(response.CompleteTime >= response.RequestTime);
        }

        [Fact]
        public async Task EmptyTarget_ReturnsStatus2()
        {
            var response = await DistributeAsync(CreateOptions(), string.Empty, null);

            Assert.Equal(2, response.Status);
        }

        [Fact]
        public async Task Target_IsCaseInsensitive()
        {
            var response = await DistributeAsync(CreateOptions(), "WsTest.Echo", "{\"text\":\"X\"}");

            Assert.Equal(0, response.Status);
            Assert.Equal("echo:X", response.Body);
        }

        [Fact]
        public async Task EndpointClassMissing_ReturnsStatus2()
        {
            var options = CreateOptions();
            // Method table knows the path but the endpoint table does not -> NotFound
            options.WatchAssemblyContext.WatchEndPoint = Array.Empty<WebSocketEndPoint>();

            var response = await DistributeAsync(options, "wstest.echo", "{\"text\":\"X\"}");

            Assert.Equal(2, response.Status);
        }

        #endregion

        #region Parameter binding

        [Fact]
        public async Task SyncInvoke_SingleStringParam_Bound()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.echo", "{\"text\":\"hello\"}");

            Assert.Equal(0, response.Status);
            Assert.Equal("echo:hello", response.Body);
            Assert.Equal("req-1", response.Id);
            Assert.Equal("wstest.echo", response.Target);
        }

        [Fact]
        public async Task SyncInvoke_MultipleBasicParams_Bound()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.add", "{\"a\":3,\"b\":4}");

            Assert.Equal(0, response.Status);
            Assert.Equal(7, response.Body);
        }

        [Fact]
        public async Task ParamBinding_IsCaseInsensitive()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.add", "{\"A\":10,\"B\":20}");

            Assert.Equal(0, response.Status);
            Assert.Equal(30, response.Body);
        }

        [Fact]
        public async Task MissingParam_MultiParamMethod_UsesNullForMissing()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.add", "{\"a\":5,\"unrelated\":1}");

            // "b" is missing -> arg stays null -> reflection invoke substitutes default(int) == 0.
            // Pins current behavior of the reflection-based method invoker.
            Assert.Equal(0, response.Status);
            Assert.Equal(5, response.Body);
        }

        [Fact]
        public async Task NullBody_ParamsGetTypeDefaults()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.add", null);

            Assert.Equal(0, response.Status);
            Assert.Equal(0, response.Body); // 0 + 0
        }

        [Fact]
        public async Task EmptyBody_OptionalParams_UseDeclaredDefaults()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.withdefaults", "{}");

            Assert.Equal(0, response.Status);
            Assert.Equal("42:d", response.Body);
        }

        [Fact]
        public async Task NullBody_ReferenceParam_IsNull()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.takeobject", null);

            Assert.Equal(0, response.Status);
            Assert.Equal("null", response.Body);
        }

        [Fact]
        public async Task SingleObjectParam_DirectBindByParameterName()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.takeobject", "{\"input\":{\"Text\":\"t\",\"Number\":8}}");

            Assert.Equal(0, response.Status);
            Assert.Equal("t#8", response.Body);
        }

        [Fact]
        public async Task SingleObjectParam_DirectBind_CaseInsensitiveParamName()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.takeobject", "{\"INPUT\":{\"Text\":\"t\",\"Number\":9}}");

            Assert.Equal(0, response.Status);
            Assert.Equal("t#9", response.Body);
        }

        [Fact]
        public async Task SingleObjectParam_FlattenedProperties_AreExpanded()
        {
            // No "input" property -> the handler expands top-level body properties
            // onto the parameter object's properties (case-insensitive)
            var response = await DistributeAsync(CreateOptions(), "wstest.takeobject", "{\"text\":\"flat\",\"number\":3}");

            Assert.Equal(0, response.Status);
            Assert.Equal("flat#3", response.Body);
        }

        [Fact]
        public async Task ParameterlessMethod_WithBody_Invokes()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.noparams", "{\"ignored\":true}");

            Assert.Equal(0, response.Status);
            Assert.Equal("noparams", response.Body);
        }

        [Fact]
        public async Task ParameterlessMethod_NullBody_Invokes()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.noparams", null);

            Assert.Equal(0, response.Status);
            Assert.Equal("noparams", response.Body);
        }

        #endregion

        #region Async endpoints

        [Fact]
        public async Task AsyncTaskOfT_ResultUnwrappedViaGetter()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.echoasync", "{\"text\":\"zz\"}");

            Assert.Equal(0, response.Status);
            Assert.Equal("async:zz", response.Body);
        }

        [Fact]
        public async Task AsyncPlainTask_BodyIsNull()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.plaintaskasync", null);

            Assert.Equal(0, response.Status);
            Assert.Null(response.Body);
        }

        [Fact]
        public async Task AsyncTaskOfT_NoRegisteredGetter_BodyIsNull()
        {
            var options = CreateOptions();
            // Remove the cached Task<T> result getter: the dispatcher then has no way to
            // read Result and pins the current fallback of returning null.
            options.WatchAssemblyContext.MethodTaskResultGetters.Clear();

            var response = await DistributeAsync(options, "wstest.echoasync", "{\"text\":\"zz\"}");

            Assert.Equal(0, response.Status);
            Assert.Null(response.Body);
        }

        #endregion

        #region Exceptions

        [Fact]
        public async Task EndpointThrows_ReturnsStatus1_WithMessage()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.throw", null, id: "err-1");

            Assert.Equal(1, response.Status);
            Assert.Equal("err-1", response.Id);
            Assert.Equal("wstest.throw", response.Target);
            // Note: sync endpoint exceptions surface as TargetInvocationException (the
            // dispatcher only unwraps AggregateException), so Msg carries the wrapper's
            // message, not "boom-endpoint". Pins current behavior.
            Assert.Contains("wstest.throw", response.Msg);
        }

        [Fact]
        public async Task AsyncEndpointThrows_ReturnsStatus1_InnerExceptionUnwrapped()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.throwasync", null);

            Assert.Equal(1, response.Status);
            Assert.Contains("boom-async-endpoint", response.Msg);
        }

        [Fact]
        public async Task EndpointThrows_OnExceptionHook_CanReplaceResponse()
        {
            var options = CreateOptions();
            Exception observed = null;
            var custom = new MvcResponseScheme { Status = 99, Msg = "custom" };
            options.ExceptionEvent += (ex, request, resp, ctx, opt, channel, logger) =>
            {
                observed = ex;
                return Task.FromResult(custom);
            };

            var response = await DistributeAsync(options, "wstest.throw", null);

            Assert.Same(custom, response);
            Assert.NotNull(observed);
            // Sync endpoint exceptions arrive wrapped in TargetInvocationException;
            // the original exception is its InnerException. Pins current behavior.
            Assert.Contains("boom-endpoint", observed.InnerException?.Message ?? observed.Message);
        }

        #endregion

        #region Injection

        [Fact]
        public async Task HttpContextAndWebSocket_AreInjectedIntoController()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.injectioncheck", null);

            Assert.Equal(0, response.Status);
            Assert.Equal(true, response.Body);
        }

        [Fact]
        public async Task ConstructorDependencies_ResolvedFromServiceProvider()
        {
            var response = await DistributeAsync(CreateOptions(), "wstest.greet", "{\"name\":\"bob\"}");

            Assert.Equal(0, response.Status);
            Assert.Equal("hi bob", response.Body);
        }

        #endregion
    }

    /// <summary>
    /// Tests for MvcChannelHandler.MvcChannel_OnBeforeConnection access control integration.
    /// </summary>
    [Collection("StaticState")]
    public class MvcChannelOnBeforeConnectionTests : IDisposable
    {
        private readonly IServiceProvider _previousServices;

        public MvcChannelOnBeforeConnectionTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
        }

        private static ServiceProvider BuildProvider(AccessControlPolicy policy)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAccessControl(p =>
            {
                p.Enabled = policy.Enabled;
                p.IpWhitelist = policy.IpWhitelist;
                p.DeniedAction = policy.DeniedAction;
                p.DenialMessage = policy.DenialMessage;
                p.EnableGeoLocationLookup = false;
            });
            return services.BuildServiceProvider();
        }

        private static DefaultHttpContext ContextWithIp(string ip)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
            context.Request.Path = "/ws";
            return context;
        }

        [Fact]
        public async Task NoApplicationServices_AllowsConnection()
        {
            WebSocketRouteOption.ApplicationServices = null;
            var handler = new MvcChannelHandler();

            Assert.True(await handler.MvcChannel_OnBeforeConnection(
                ContextWithIp("1.2.3.4"), new WebSocketRouteOption(), "/ws", NullLogger<WebSocketRouteMiddleware>.Instance));
        }

        [Fact]
        public async Task NoAccessControlService_AllowsConnection()
        {
            var services = new ServiceCollection();
            using var provider = services.BuildServiceProvider();
            WebSocketRouteOption.ApplicationServices = provider;
            var handler = new MvcChannelHandler();

            Assert.True(await handler.MvcChannel_OnBeforeConnection(
                ContextWithIp("1.2.3.4"), new WebSocketRouteOption(), "/ws", NullLogger<WebSocketRouteMiddleware>.Instance));
        }

        [Fact]
        public async Task AllowedIp_ReturnsTrue()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("1.2.3.4");
            using var provider = BuildProvider(policy);
            WebSocketRouteOption.ApplicationServices = provider;
            var handler = new MvcChannelHandler();

            Assert.True(await handler.MvcChannel_OnBeforeConnection(
                ContextWithIp("1.2.3.4"), new WebSocketRouteOption(), "/ws", NullLogger<WebSocketRouteMiddleware>.Instance));
        }

        [Fact]
        public async Task DeniedIp_CloseConnectionAction_ReturnsFalse()
        {
            var policy = new AccessControlPolicy { Enabled = true, DeniedAction = AccessDeniedAction.CloseConnection };
            policy.IpWhitelist.Add("9.9.9.9");
            using var provider = BuildProvider(policy);
            WebSocketRouteOption.ApplicationServices = provider;
            var handler = new MvcChannelHandler();
            var context = ContextWithIp("1.2.3.4");

            Assert.False(await handler.MvcChannel_OnBeforeConnection(
                context, new WebSocketRouteOption(), "/ws", NullLogger<WebSocketRouteMiddleware>.Instance));
            Assert.Equal(200, context.Response.StatusCode); // untouched
        }

        [Fact]
        public async Task DeniedIp_ReturnForbidden_Sets403()
        {
            var policy = new AccessControlPolicy
            {
                Enabled = true,
                DeniedAction = AccessDeniedAction.ReturnForbidden,
                DenialMessage = "denied-msg"
            };
            policy.IpWhitelist.Add("9.9.9.9");
            using var provider = BuildProvider(policy);
            WebSocketRouteOption.ApplicationServices = provider;
            var handler = new MvcChannelHandler();
            var context = ContextWithIp("1.2.3.4");

            Assert.False(await handler.MvcChannel_OnBeforeConnection(
                context, new WebSocketRouteOption(), "/ws", NullLogger<WebSocketRouteMiddleware>.Instance));
            Assert.Equal(403, context.Response.StatusCode);
        }

        [Fact]
        public async Task DeniedIp_ReturnUnauthorized_Sets401()
        {
            var policy = new AccessControlPolicy
            {
                Enabled = true,
                DeniedAction = AccessDeniedAction.ReturnUnauthorized
            };
            policy.IpWhitelist.Add("9.9.9.9");
            using var provider = BuildProvider(policy);
            WebSocketRouteOption.ApplicationServices = provider;
            var handler = new MvcChannelHandler();
            var context = ContextWithIp("1.2.3.4");

            Assert.False(await handler.MvcChannel_OnBeforeConnection(
                context, new WebSocketRouteOption(), "/ws", NullLogger<WebSocketRouteMiddleware>.Instance));
            Assert.Equal(401, context.Response.StatusCode);
        }
    }
}
