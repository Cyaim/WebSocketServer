using System.Text.Json.Nodes;
using Cyaim.WebSocketServer.Infrastructure.Attributes;
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
    /// Endpoints that take a <see cref="CancellationToken"/> alongside their payload.
    /// </summary>
    public class TokenBindingController
    {
        public sealed class EchoRequest
        {
            public string Text { get; set; }

            public int Count { get; set; }
        }

        public sealed class EchoResult
        {
            public string Text { get; set; }

            public int Count { get; set; }

            public bool TokenCanBeCanceled { get; set; }

            public bool TokenAlreadyCanceled { get; set; }
        }

        /// <summary>Payload plus a token — the shape that used to bind the payload to null.</summary>
        [WebSocket]
        public EchoResult Echo(EchoRequest request, CancellationToken cancellationToken) => new EchoResult
        {
            Text = request?.Text,
            Count = request?.Count ?? -1,
            TokenCanBeCanceled = cancellationToken.CanBeCanceled,
            TokenAlreadyCanceled = cancellationToken.IsCancellationRequested
        };

        /// <summary>Token in first position, to prove the payload index is resolved and not assumed.</summary>
        [WebSocket]
        public EchoResult EchoTokenFirst(CancellationToken cancellationToken, EchoRequest request) => new EchoResult
        {
            Text = request?.Text,
            Count = request?.Count ?? -1,
            TokenCanBeCanceled = cancellationToken.CanBeCanceled,
            TokenAlreadyCanceled = cancellationToken.IsCancellationRequested
        };

        /// <summary>Only a token: the body is empty and the endpoint still gets a live token.</summary>
        [WebSocket]
        public bool TokenOnly(CancellationToken cancellationToken) => cancellationToken.IsCancellationRequested;

        /// <summary>Two real payload parameters still bind by name, with the token filled in.</summary>
        [WebSocket]
        public string TwoValues(string first, int second, CancellationToken cancellationToken)
            => $"{first}:{second}:{cancellationToken.IsCancellationRequested}";
    }

    /// <summary>
    /// Regression tests: a <see cref="CancellationToken"/> parameter must not participate in body
    /// binding, and must be supplied from the connection.
    /// </summary>
    /// <remarks>
    /// Before the fix, <c>Echo(EchoRequest request, CancellationToken cancellationToken)</c> had two
    /// parameters, so the dispatcher left whole-body binding and tried to find a JSON property
    /// named "request". A normal payload has no such property, so the endpoint received null on
    /// every single call — the failure looked like a client bug and could not be worked around
    /// except by dropping the token parameter. The streaming dispatcher already injected the
    /// connection's token by type; this brings the MVC path in line.
    /// </remarks>
    [Collection("StaticState")]
    public class CancellationTokenBindingTests : IDisposable
    {
        private readonly IServiceProvider _previousServices;
        private readonly ServiceProvider _provider;
        private readonly MvcTestSupport.StubLifetime _lifetime = new MvcTestSupport.StubLifetime();

        public CancellationTokenBindingTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHostApplicationLifetime>(_lifetime);
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

        private Task<MvcResponseScheme> DistributeAsync(string target, string bodyJson, CancellationToken connectionToken)
        {
            var options = new WebSocketRouteOption
            {
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(TokenBindingController))
            };

            var context = new DefaultHttpContext { RequestAborted = connectionToken };
            JsonObject body = bodyJson == null ? null : JsonNode.Parse(bodyJson).AsObject();

            return MvcChannelHandler.MvcDistributeAsync(
                options,
                context,
                new TestWebSocket(),
                new MvcRequestScheme { Id = "req-1", Target = target },
                body,
                NullLogger<WebSocketRouteMiddleware>.Instance,
                _lifetime);
        }

        private static TokenBindingController.EchoResult ReadEcho(MvcResponseScheme response)
        {
            Assert.Equal(0, response.Status);
            var echo = Assert.IsType<TokenBindingController.EchoResult>(response.Body);
            return echo;
        }

        [Fact]
        public async Task Payload_binds_when_the_method_also_takes_a_cancellation_token()
        {
            var response = await DistributeAsync(
                "tokenbinding.echo",
                "{\"text\":\"hello\",\"count\":7}",
                CancellationToken.None);

            var echo = ReadEcho(response);
            Assert.Equal("hello", echo.Text);
            Assert.Equal(7, echo.Count);
        }

        [Fact]
        public async Task Payload_binds_when_the_token_is_declared_first()
        {
            var response = await DistributeAsync(
                "tokenbinding.echotokenfirst",
                "{\"text\":\"ordered\",\"count\":3}",
                CancellationToken.None);

            var echo = ReadEcho(response);
            Assert.Equal("ordered", echo.Text);
            Assert.Equal(3, echo.Count);
        }

        [Fact]
        public async Task Token_comes_from_the_connection()
        {
            using var cts = new CancellationTokenSource();

            var response = await DistributeAsync(
                "tokenbinding.echo",
                "{\"text\":\"x\",\"count\":1}",
                cts.Token);

            var echo = ReadEcho(response);

            // A token that can be cancelled proves it is the connection's, not default(CancellationToken).
            Assert.True(echo.TokenCanBeCanceled);
            Assert.False(echo.TokenAlreadyCanceled);
        }

        [Fact]
        public async Task Already_cancelled_connection_token_reaches_the_endpoint()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var response = await DistributeAsync(
                "tokenbinding.echo",
                "{\"text\":\"x\",\"count\":1}",
                cts.Token);

            Assert.True(ReadEcho(response).TokenAlreadyCanceled);
        }

        [Fact]
        public async Task Token_only_endpoint_receives_the_connection_token_with_an_empty_body()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var response = await DistributeAsync("tokenbinding.tokenonly", null, cts.Token);

            Assert.Equal(0, response.Status);
            Assert.True(Assert.IsType<bool>(response.Body));
        }

        [Fact]
        public async Task Multiple_payload_parameters_still_bind_by_name()
        {
            var response = await DistributeAsync(
                "tokenbinding.twovalues",
                "{\"first\":\"a\",\"second\":42}",
                CancellationToken.None);

            Assert.Equal(0, response.Status);
            Assert.Equal("a:42:False", response.Body);
        }
    }
}
