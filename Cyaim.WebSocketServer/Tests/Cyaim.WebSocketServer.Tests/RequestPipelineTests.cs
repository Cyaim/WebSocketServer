using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Http;

namespace Cyaim.WebSocketServer.Tests
{
    public class RequestPipelineTests
    {
        #region PipelineContext pooling

        [Fact]
        public void PipelineContext_CreateBasic_SetsFields()
        {
            var httpContext = new DefaultHttpContext();
            var options = new WebSocketRouteOption();

            var ctx = PipelineContext.CreateBasic(httpContext, options);
            try
            {
                Assert.Same(httpContext, ctx.HttpContext);
                Assert.Same(options, ctx.WebSocketOptions);
                Assert.Null(ctx.WebSocket);
                Assert.Null(ctx.Data);
            }
            finally
            {
                ctx.Return();
            }
        }

        [Fact]
        public void PipelineContext_ReturnAndCreate_ReusesPooledInstance_AndClearsState()
        {
            var httpContext = new DefaultHttpContext();
            var options = new WebSocketRouteOption();
            var ws = new TestWebSocket();
            var data = new byte[] { 1, 2, 3 };
            var request = new MvcRequestScheme { Id = "1", Target = "t" };

            var first = PipelineContext.CreateForward(httpContext, ws, null, data, request, null, options);
            first.Return();

            // DefaultObjectPool returns the most recently returned instance on the same thread
            var second = PipelineContext.CreateBasic(httpContext, options);
            try
            {
                Assert.Same(first, second);
                // The pool policy must have cleared all references on Return
                Assert.Null(second.WebSocket);
                Assert.Null(second.Data);
                Assert.Null(second.Request);
                Assert.Null(second.RequestBody);
                Assert.Null(second.ReceiveResult);
                Assert.Same(httpContext, second.HttpContext);
            }
            finally
            {
                second.Return();
            }
        }

        [Fact]
        public void PipelineContext_CreateReceive_SetsReceiveFields()
        {
            var httpContext = new DefaultHttpContext();
            var ws = new TestWebSocket();
            var data = new byte[] { 9, 8 };

            var ctx = PipelineContext.CreateReceive(httpContext, ws, null, data);
            try
            {
                Assert.Same(httpContext, ctx.HttpContext);
                Assert.Same(ws, ctx.WebSocket);
                Assert.Same(data, ctx.Data);
                Assert.Null(ctx.WebSocketOptions);
            }
            finally
            {
                ctx.Return();
            }
        }

        [Fact]
        public void PipelineContext_CreateForward_SetsAllFields()
        {
            var httpContext = new DefaultHttpContext();
            var options = new WebSocketRouteOption();
            var ws = new TestWebSocket();
            var request = new MvcRequestScheme { Target = "x.y" };
            var body = new System.Text.Json.Nodes.JsonObject { ["k"] = 1 };

            var ctx = PipelineContext.CreateForward(httpContext, ws, null, new byte[] { 1 }, request, body, options);
            try
            {
                Assert.Same(request, ctx.Request);
                Assert.Same(body, ctx.RequestBody);
                Assert.Same(options, ctx.WebSocketOptions);
            }
            finally
            {
                ctx.Return();
            }
        }

        #endregion

        #region DelegateRequestPipeline

        [Fact]
        public void DelegateRequestPipeline_NullHandler_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new DelegateRequestPipeline(null));
        }

        [Fact]
        public async Task DelegateRequestPipeline_InvokesHandler()
        {
            PipelineContext observed = null;
            var pipeline = new DelegateRequestPipeline(ctx => { observed = ctx; return Task.CompletedTask; });
            var context = PipelineContext.CreateBasic(new DefaultHttpContext(), null);
            try
            {
                await pipeline.InvokeAsync(context);
                Assert.Same(context, observed);
            }
            finally
            {
                context.Return();
            }
        }

        #endregion

        #region MvcChannelHandler.AddRequestMiddleware ordering

        private static DelegateRequestPipeline Noop() => new DelegateRequestPipeline(_ => Task.CompletedTask);

        [Fact]
        public void AddRequestMiddleware_OrderAutoIncrements_FromZero()
        {
            var handler = new MvcChannelHandler();

            handler.AddRequestMiddleware(RequestPipelineStage.BeforeForwardingData, Noop());
            handler.AddRequestMiddleware(RequestPipelineStage.BeforeForwardingData, Noop());
            handler.AddRequestMiddleware(RequestPipelineStage.BeforeForwardingData, Noop());

            var queue = handler.RequestPipeline[RequestPipelineStage.BeforeForwardingData];
            var orders = queue.Select(x => x.Order).ToArray();
            Assert.Equal(new float[] { 0, 1, 2 }, orders);
        }

        [Fact]
        public void AddRequestMiddleware_ExplicitOrder_IsHonored_AndAutoIncrementContinuesFromMax()
        {
            var handler = new MvcChannelHandler();

            handler.AddRequestMiddleware(RequestPipelineStage.ReceivingData, Noop(), 10f);
            handler.AddRequestMiddleware(RequestPipelineStage.ReceivingData, Noop());

            var orders = handler.RequestPipeline[RequestPipelineStage.ReceivingData].Select(x => x.Order).ToArray();
            Assert.Equal(new float[] { 10, 11 }, orders);
        }

        [Fact]
        public void AddRequestMiddleware_StagesAreIndependent()
        {
            var handler = new MvcChannelHandler();

            handler.AddRequestMiddleware(RequestPipelineStage.Connected, Noop());
            handler.AddRequestMiddleware(RequestPipelineStage.Disconnected, Noop());

            Assert.Equal(0f, handler.RequestPipeline[RequestPipelineStage.Connected].Single().Order);
            Assert.Equal(0f, handler.RequestPipeline[RequestPipelineStage.Disconnected].Single().Order);
        }

        [Fact]
        public void AddRequestMiddleware_WithPipelineItem_StageOverriddenByParameter()
        {
            var handler = new MvcChannelHandler();
            var item = new PipelineItem { Item = Noop(), Stage = RequestPipelineStage.Connected, Order = 5 };

            var queue = handler.AddRequestMiddleware(RequestPipelineStage.AfterForwardingData, item);

            Assert.Equal(RequestPipelineStage.AfterForwardingData, item.Stage);
            Assert.Same(item, queue.Single());
            Assert.True(handler.RequestPipeline.ContainsKey(RequestPipelineStage.AfterForwardingData));
        }

        [Fact]
        public void AddRequestMiddleware_WithPipelineItem_UsesItemStage()
        {
            var handler = new MvcChannelHandler();
            var item = new PipelineItem { Item = Noop(), Stage = RequestPipelineStage.Connected };

            var pipeline = handler.AddRequestMiddleware(item);

            Assert.Same(handler.RequestPipeline, pipeline);
            Assert.Same(item, handler.RequestPipeline[RequestPipelineStage.Connected].Single());
        }

        [Fact]
        public void DictionaryExtension_AddRequestMiddleware_DelegateOverload()
        {
            var handler = new MvcChannelHandler();

            handler.RequestPipeline.AddRequestMiddleware(RequestPipelineStage.BeforeReceivingData, _ => Task.CompletedTask, 3.5f);

            var item = handler.RequestPipeline[RequestPipelineStage.BeforeReceivingData].Single();
            Assert.Equal(3.5f, item.Order);
            Assert.IsType<DelegateRequestPipeline>(item.Item);
        }

        #endregion
    }
}
