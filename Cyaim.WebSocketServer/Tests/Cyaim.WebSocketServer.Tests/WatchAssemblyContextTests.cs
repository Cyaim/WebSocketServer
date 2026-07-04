using Cyaim.WebSocketServer.Infrastructure.Configures;

namespace Cyaim.WebSocketServer.Tests
{
    public class WatchAssemblyContextTests
    {
        private class EndpointA { }
        private class EndpointB { }

        private static WatchAssemblyContext CreateContext()
        {
            return new WatchAssemblyContext
            {
                WatchEndPoint = new[]
                {
                    new WebSocketEndPoint { MethodPath = "a.echo", Class = typeof(EndpointA) },
                    new WebSocketEndPoint { MethodPath = "b.echo", Class = typeof(EndpointB) },
                }
            };
        }

        [Fact]
        public void GetEndpointClass_ReturnsDeclaringClass()
        {
            var ctx = CreateContext();

            Assert.Same(typeof(EndpointA), ctx.GetEndpointClass("a.echo"));
            Assert.Same(typeof(EndpointB), ctx.GetEndpointClass("b.echo"));
        }

        [Fact]
        public void GetEndpointClass_Miss_ReturnsNull()
        {
            var ctx = CreateContext();

            Assert.Null(ctx.GetEndpointClass("does.notexist"));
        }

        [Fact]
        public void GetEndpointClass_NullMethodPath_ReturnsNull()
        {
            var ctx = CreateContext();

            Assert.Null(ctx.GetEndpointClass(null));
        }

        [Fact]
        public void GetEndpointClass_NullWatchEndPoint_ReturnsNull()
        {
            var ctx = new WatchAssemblyContext { WatchEndPoint = null };

            Assert.Null(ctx.GetEndpointClass("a.echo"));
        }

        [Fact]
        public void GetEndpointClass_EndpointsWithNullPathOrClass_AreSkipped()
        {
            var ctx = new WatchAssemblyContext
            {
                WatchEndPoint = new[]
                {
                    new WebSocketEndPoint { MethodPath = null, Class = typeof(EndpointA) },
                    new WebSocketEndPoint { MethodPath = "b.echo", Class = null },
                    null,
                    new WebSocketEndPoint { MethodPath = "ok.echo", Class = typeof(EndpointB) },
                }
            };

            Assert.Null(ctx.GetEndpointClass("b.echo"));
            Assert.Same(typeof(EndpointB), ctx.GetEndpointClass("ok.echo"));
        }

        [Fact]
        public void GetEndpointClass_LookupIsCaseInsensitive()
        {
            var ctx = CreateContext();

            // Endpoint paths are stored lowercase by the library;
            // the lookup map uses StringComparer.OrdinalIgnoreCase
            Assert.Same(typeof(EndpointA), ctx.GetEndpointClass("A.Echo"));
            Assert.Same(typeof(EndpointA), ctx.GetEndpointClass("A.ECHO"));
        }

        [Fact]
        public void GetEndpointClass_RepeatedCalls_ReturnConsistentResults()
        {
            var ctx = CreateContext();

            for (int i = 0; i < 3; i++)
            {
                Assert.Same(typeof(EndpointA), ctx.GetEndpointClass("a.echo"));
                Assert.Null(ctx.GetEndpointClass("nope"));
            }
        }

        [Fact]
        public void GetEndpointClass_ConcurrentFirstAccess_IsSafe()
        {
            var ctx = CreateContext();

            var results = new Type[32];
            Parallel.For(0, results.Length, i => results[i] = ctx.GetEndpointClass("a.echo"));

            Assert.All(results, t => Assert.Same(typeof(EndpointA), t));
        }
    }
}
