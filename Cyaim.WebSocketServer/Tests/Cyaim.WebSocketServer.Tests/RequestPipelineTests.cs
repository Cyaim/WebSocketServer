using System.Collections.Generic;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Http;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Tests for the compiled middleware chain (Option A) that replaced the stage-keyed pipeline.
    /// 编译式中间件责任链（Option A，替代原阶段管道）的测试。
    /// </summary>
    public class RequestPipelineTests
    {
        private static WebSocketMessageContext NewContext(WebSocketRouteOption options)
            => new WebSocketMessageContext
            {
                HttpContext = new DefaultHttpContext(),
                Options = options,
                Request = new MvcRequestScheme { Id = "1", Target = "t" },
            };

        [Fact]
        public void MiddlewareCount_ReflectsRegistrations()
        {
            var options = new WebSocketRouteOption();
            Assert.Equal(0, options.MiddlewareCount);

            options.Use((ctx, next) => next(ctx));
            options.Use((ctx, next) => next(ctx));

            Assert.Equal(2, options.MiddlewareCount);
        }

        [Fact]
        public void Use_NullMiddleware_Throws()
        {
            var options = new WebSocketRouteOption();
            Assert.Throws<System.ArgumentNullException>(() => options.Use((System.Func<WebSocketMessageContext, WebSocketRequestDelegate, System.Threading.Tasks.Task>)null));
        }

        [Fact]
        public async Task BuildPipeline_NoMiddleware_RunsTerminalDirectly()
        {
            var options = new WebSocketRouteOption();
            bool terminalRan = false;
            var pipeline = options.BuildPipeline(ctx => { terminalRan = true; ctx.Response = "done"; return Task.CompletedTask; });

            var context = NewContext(options);
            await pipeline(context);

            Assert.True(terminalRan);
            Assert.Equal("done", context.Response);
        }

        [Fact]
        public async Task Middleware_RunOuterToInner_InRegistrationOrder()
        {
            var options = new WebSocketRouteOption();
            var log = new List<string>();

            options.Use(async (ctx, next) => { log.Add("A-before"); await next(ctx); log.Add("A-after"); });
            options.Use(async (ctx, next) => { log.Add("B-before"); await next(ctx); log.Add("B-after"); });

            var pipeline = options.BuildPipeline(ctx => { log.Add("terminal"); return Task.CompletedTask; });
            await pipeline(NewContext(options));

            // First-registered (A) is outermost; wraps B; B wraps terminal.
            Assert.Equal(new[] { "A-before", "B-before", "terminal", "B-after", "A-after" }, log.ToArray());
        }

        [Fact]
        public async Task Middleware_ShortCircuit_SkipsTerminal()
        {
            var options = new WebSocketRouteOption();
            bool terminalRan = false;

            options.Use((ctx, next) =>
            {
                // Do not call next -> the endpoint (terminal) must not run.
                ctx.Response = "short-circuited";
                return Task.CompletedTask;
            });

            var pipeline = options.BuildPipeline(ctx => { terminalRan = true; ctx.Response = "terminal"; return Task.CompletedTask; });
            var context = NewContext(options);
            await pipeline(context);

            Assert.False(terminalRan);
            Assert.Equal("short-circuited", context.Response);
        }

        [Fact]
        public async Task Middleware_CanModifyResponse_AfterTerminal()
        {
            var options = new WebSocketRouteOption();

            options.Use(async (ctx, next) =>
            {
                await next(ctx);
                ctx.Response = ctx.Response + "+wrapped";
            });

            var pipeline = options.BuildPipeline(ctx => { ctx.Response = "endpoint"; return Task.CompletedTask; });
            var context = NewContext(options);
            await pipeline(context);

            Assert.Equal("endpoint+wrapped", context.Response);
        }

        [Fact]
        public async Task Middleware_CanShareStateViaItems()
        {
            var options = new WebSocketRouteOption();

            options.Use(async (ctx, next) => { ctx.Items["k"] = 42; await next(ctx); });

            object seen = null;
            var pipeline = options.BuildPipeline(ctx => { ctx.Items.TryGetValue("k", out seen); return Task.CompletedTask; });
            await pipeline(NewContext(options));

            Assert.Equal(42, seen);
        }

        private sealed class CountingMiddleware : IWebSocketMiddleware
        {
            public int Calls;
            public Task InvokeAsync(WebSocketMessageContext context, WebSocketRequestDelegate next)
            {
                Calls++;
                return next(context);
            }
        }

        [Fact]
        public async Task Use_IWebSocketMiddlewareInstance_IsInvoked()
        {
            var options = new WebSocketRouteOption();
            var mw = new CountingMiddleware();
            options.Use(mw);

            var pipeline = options.BuildPipeline(ctx => Task.CompletedTask);
            await pipeline(NewContext(options));

            Assert.Equal(1, mw.Calls);
        }

        [Fact]
        public void BuildPipeline_NullTerminal_Throws()
        {
            var options = new WebSocketRouteOption();
            Assert.Throws<System.ArgumentNullException>(() => options.BuildPipeline(null));
        }
    }
}
