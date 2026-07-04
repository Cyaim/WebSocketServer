using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Controllers used to drive serialization-failure and dispatch branches.
    /// </summary>
    public static class CovControllers
    {
        public sealed class SelfRef
        {
            public SelfRef Loop { get; set; }
        }

        public class CyclicController
        {
            // Returns a self-referencing object graph; System.Text.Json throws JsonException on it.
            public SelfRef Cycle()
            {
                var n = new SelfRef();
                n.Loop = n;
                return n;
            }

            public string Plain() => "ok";

            // Second param is a DateTime; an invalid date string makes ConvertTo throw FormatException.
            public string TakeDate(int a, System.DateTime d) => a + ":" + d.Year;
        }
    }

    /// <summary>
    /// Reflection-driven line coverage for MvcChannelHandler internals that the receive loop
    /// no longer reaches: the legacy MvcForwardSendData overloads, the OnDisconnected close-status
    /// switch, InvokePipeline error handling, and a couple of MvcDistributeAsync binding branches.
    /// Touches WebSocketRouteOption.ApplicationServices, so it runs in StaticState and restores it.
    /// </summary>
    [Collection("StaticState")]
    public class MvcChannelHandlerCovTests : IDisposable
    {
        private readonly IServiceProvider _previousServices;
        private readonly ServiceProvider _provider;
        private readonly MvcTestSupport.StubLifetime _lifetime = new MvcTestSupport.StubLifetime();

        public MvcChannelHandlerCovTests()
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

        private static WebSocketRouteOption Options()
            => new WebSocketRouteOption
            {
                WatchAssemblyContext = MvcTestSupport.BuildContext(
                    typeof(MvcTestSupport.WsTestController),
                    typeof(CovControllers.CyclicController))
            };

        private MvcChannelHandler NewHandler(WebSocketRouteOption options)
        {
            var handler = new MvcChannelHandler();
            typeof(MvcChannelHandler).GetField("logger", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(handler, NullLogger<WebSocketRouteMiddleware>.Instance);
            typeof(MvcChannelHandler).GetField("webSocketOption", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(handler, options);
            return handler;
        }

        private static MethodInfo ForwardOverload(params Type[] paramTypes)
        {
            foreach (var m in typeof(MvcChannelHandler).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                         .Where(x => x.Name == "MvcForwardSendData"))
            {
                var ps = m.GetParameters().Select(p => p.ParameterType).ToArray();
                if (ps.SequenceEqual(paramTypes))
                {
                    return m;
                }
            }
            Assert.Fail("overload not found");
            return null;
        }

        private Task InvokeForward(MethodInfo m, MvcChannelHandler handler, object[] args)
            => (Task)m.Invoke(handler, args);

        private static DefaultHttpContext CtxWithId()
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.Id = Guid.NewGuid().ToString("N");
            return ctx;
        }

        private static WebSocketReceiveResult TextResult() => new WebSocketReceiveResult(10, WebSocketMessageType.Text, true);
        private static WebSocketReceiveResult CloseResult() => new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);

        #region MvcDistributeAsync binding branches

        [Fact]
        public async Task Distribute_FlattenedExactCaseProps_AreBound()
        {
            // Body without an "input" wrapper and using the exact property names hits the
            // exact-name SetValue branch (case-insensitive retry is skipped).
            var options = Options();
            var request = new MvcRequestScheme { Id = "1", Target = "wstest.takeobject" };
            var body = JsonNode.Parse("{\"Text\":\"t\",\"Number\":5}").AsObject();

            var resp = await MvcChannelHandler.MvcDistributeAsync(options, new DefaultHttpContext(), new TestWebSocket(),
                request, body, NullLogger<WebSocketRouteMiddleware>.Instance, _lifetime);

            Assert.Equal(0, resp.Status);
            Assert.Equal("t#5", resp.Body);
        }

        #endregion

        #region Main MvcForwardSendData(request, requestBody) overload

        private static readonly Type[] MainSig =
        {
            typeof(WebSocket), typeof(HttpContext), typeof(WebSocketReceiveResult),
            typeof(MvcRequestScheme), typeof(JsonObject), typeof(long), typeof(IHostApplicationLifetime)
        };

        [Fact]
        public async Task MainForward_CloseMessage_ReturnsEarly()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(MainSig);
            await InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(), new DefaultHttpContext(), CloseResult(),
                new MvcRequestScheme { Target = "wstest.echo" }, null, 0L, _lifetime
            });
        }

        [Fact]
        public async Task MainForward_CyclicResult_JsonException_IsCaught()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(MainSig);
            // Cyclic endpoint result makes SerializeToUtf8Bytes throw JsonException -> caught.
            await InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(), new DefaultHttpContext(), TextResult(),
                new MvcRequestScheme { Id = "1", Target = "cyclic.cycle" }, null, 0L, _lifetime
            });
        }

        [Fact]
        public async Task MainForward_ClosedSocketSend_GeneralCatch_Rethrows()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(MainSig);
            // Serialization succeeds but sending to a non-open socket throws ArgumentNullException,
            // which the non-Json catch rethrows.
            await Assert.ThrowsAnyAsync<Exception>(() => InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(WebSocketState.Closed), new DefaultHttpContext(), TextResult(),
                new MvcRequestScheme { Id = "1", Target = "wstest.echo" },
                JsonNode.Parse("{\"text\":\"x\"}").AsObject(), 0L, _lifetime
            }));
        }

        #endregion

        #region Legacy MvcForwardSendData(request) overload

        private static readonly Type[] ReqSig =
        {
            typeof(WebSocket), typeof(HttpContext), typeof(WebSocketReceiveResult),
            typeof(MvcRequestScheme), typeof(long), typeof(IHostApplicationLifetime)
        };

        [Fact]
        public async Task LegacyReqForward_CloseMessage_ReturnsEarly()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(ReqSig);
            await InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(), new DefaultHttpContext(), CloseResult(),
                new MvcRequestScheme { Target = "wstest.echo" }, 0L, _lifetime
            });
        }

        [Fact]
        public async Task LegacyReqForward_ValidRequest_ReserializesBody_AndSends()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(ReqSig);
            var request = new MvcRequestScheme
            {
                Id = "1",
                Target = "wstest.echo",
                Body = JsonNode.Parse("{\"text\":\"hi\"}")
            };
            await InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(), new DefaultHttpContext(), TextResult(), request, 0L, _lifetime
            });
        }

        [Fact]
        public async Task LegacyReqForward_CyclicRequestBody_JsonException_IsCaught()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(ReqSig);
            var loop = new CovControllers.SelfRef();
            loop.Loop = loop;
            var request = new MvcRequestScheme { Id = "1", Target = "wstest.echo", Body = loop };
            await InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(), new DefaultHttpContext(), TextResult(), request, 0L, _lifetime
            });
        }

        [Fact]
        public async Task LegacyReqForward_ClosedSocket_GeneralCatch_Rethrows()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(ReqSig);
            var request = new MvcRequestScheme
            {
                Id = "1",
                Target = "wstest.echo",
                Body = JsonNode.Parse("{\"text\":\"hi\"}")
            };
            await Assert.ThrowsAnyAsync<Exception>(() => InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(WebSocketState.Closed), new DefaultHttpContext(), TextResult(), request, 0L, _lifetime
            }));
        }

        #endregion

        #region Legacy MvcForwardSendData(StringBuilder) overload

        private static readonly Type[] SbSig =
        {
            typeof(WebSocket), typeof(HttpContext), typeof(WebSocketReceiveResult),
            typeof(StringBuilder), typeof(long), typeof(IHostApplicationLifetime)
        };

        [Fact]
        public async Task LegacySbForward_CloseMessage_ReturnsEarly()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(SbSig);
            await InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(), new DefaultHttpContext(), CloseResult(), new StringBuilder("{}"), 0L, _lifetime
            });
        }

        [Fact]
        public async Task LegacySbForward_NullJson_LogsAndReturns()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(SbSig);
            // "null" deserializes to a null request -> the null-request branch logs and returns.
            await InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(), new DefaultHttpContext(), TextResult(), new StringBuilder("null"), 0L, _lifetime
            });
        }

        [Fact]
        public async Task LegacySbForward_ValidJson_DispatchesThroughRequestOverload()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(SbSig);
            await InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(), new DefaultHttpContext(), TextResult(),
                new StringBuilder("{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"hi\"}}"), 0L, _lifetime
            });
        }

        [Fact]
        public async Task LegacySbForward_InvalidJson_JsonException_IsCaught()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(SbSig);
            await InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(), new DefaultHttpContext(), TextResult(), new StringBuilder("{ not json"), 0L, _lifetime
            });
        }

        [Fact]
        public async Task LegacySbForward_ClosedSocket_GeneralCatch_Rethrows()
        {
            var handler = NewHandler(Options());
            var m = ForwardOverload(SbSig);
            await Assert.ThrowsAnyAsync<Exception>(() => InvokeForward(m, handler, new object[]
            {
                new TestWebSocket(WebSocketState.Closed), new DefaultHttpContext(), TextResult(),
                new StringBuilder("{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"hi\"}}"), 0L, _lifetime
            }));
        }

        #endregion

        #region OnDisconnected close-status switch

        [Fact]
        public async Task OnDisconnected_AllCloseStatuses_AndNull_HitEverySwitchArm()
        {
            var handler = NewHandler(Options());
            var m = typeof(MvcChannelHandler).GetMethod("MvcChannel_OnDisconnected",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { typeof(HttpContext), typeof(WebSocketCloseStatus?), typeof(WebSocketRouteOption), typeof(ILogger<WebSocketRouteMiddleware>) });
            Assert.NotNull(m);

            var options = Options();
            foreach (WebSocketCloseStatus status in Enum.GetValues<WebSocketCloseStatus>())
            {
                await (Task)m.Invoke(handler, new object[]
                {
                    CtxWithId(), (WebSocketCloseStatus?)status, options, NullLogger<WebSocketRouteMiddleware>.Instance
                });
            }
            // null status -> "connection shutdown" branch
            await (Task)m.Invoke(handler, new object[]
            {
                CtxWithId(), (WebSocketCloseStatus?)null, options, NullLogger<WebSocketRouteMiddleware>.Instance
            });
        }

        [Fact]
        public async Task OnDisconnected_DisconnectedEventThrows_IsCaught()
        {
            var handler = NewHandler(Options());
            var options = Options();
            options.DisconnectedEvent += (ctx, opt, channel, logger) => throw new InvalidOperationException("disc-throws");
            var m = typeof(MvcChannelHandler).GetMethod("MvcChannel_OnDisconnected",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { typeof(HttpContext), typeof(WebSocketCloseStatus?), typeof(WebSocketRouteOption), typeof(ILogger<WebSocketRouteMiddleware>) });

            await (Task)m.Invoke(handler, new object[]
            {
                CtxWithId(), (WebSocketCloseStatus?)WebSocketCloseStatus.NormalClosure, options,
                NullLogger<WebSocketRouteMiddleware>.Instance
            });
        }

        #endregion

        #region InvokePipeline

        [Fact]
        public async Task InvokePipeline_UnknownStage_ReturnsNull()
        {
            var handler = NewHandler(Options());
            var m = typeof(MvcChannelHandler).GetMethod("InvokePipeline", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);
            // No queue registered for this stage -> returns null (context is unused on that path).
            var task = (Task)m.Invoke(handler, new object[] { RequestPipelineStage.Connected, null });
            await task;
        }

        [Fact]
        public async Task InvokePipeline_HandlerThrows_ExceptionCapturedOnItem()
        {
            var handler = NewHandler(Options());
            handler.RequestPipeline.AddRequestMiddleware(RequestPipelineStage.Connected, _ =>
                throw new InvalidOperationException("pipeline-throws"));

            var m = typeof(MvcChannelHandler).GetMethod("InvokePipeline", BindingFlags.NonPublic | BindingFlags.Instance);
            var ctx = PipelineContext.CreateBasic(new DefaultHttpContext(), Options());
            var task = (Task)m.Invoke(handler, new object[] { RequestPipelineStage.Connected, ctx });
            await task;

            var queue = handler.RequestPipeline[RequestPipelineStage.Connected];
            Assert.Contains(queue, item => item.Exception != null);
        }

        #endregion

        #region FindJsonPropertyValue

        [Fact]
        public void FindJsonPropertyValue_TargetFound_AndVariousShapes()
        {
            var handler = NewHandler(Options());
            Assert.Equal("wstest.echo", handler.FindJsonPropertyValue(Encoding.UTF8.GetBytes("{\"target\":\"wstest.echo\"}")));
            // Case-insensitive match on the property name (length-equal branch).
            Assert.Equal("x", handler.FindJsonPropertyValue(Encoding.UTF8.GetBytes("{\"TARGET\":\"x\"}")));
            // Target present but its value is not a string -> null.
            Assert.Null(handler.FindJsonPropertyValue(Encoding.UTF8.GetBytes("{\"target\":123}")));
            // No target property -> null.
            Assert.Null(handler.FindJsonPropertyValue(Encoding.UTF8.GetBytes("{\"other\":\"y\"}")));
            // Malformed value right after a matched "target" name makes the inner Read() throw,
            // which the inner try/catch swallows -> returns null.
            Assert.Null(handler.FindJsonPropertyValue(Encoding.UTF8.GetBytes("{\"target\":@}")));
        }

        #endregion
    }
}
