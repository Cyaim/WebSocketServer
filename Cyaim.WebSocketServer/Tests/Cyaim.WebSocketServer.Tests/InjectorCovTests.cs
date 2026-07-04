using System.Net.WebSockets;
using System.Reflection;
using System.Reflection.Emit;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Injectors;
using Microsoft.AspNetCore.Http;

namespace Cyaim.WebSocketServer.Tests.GenLookup
{
    // The factories look for "{ClassName}_{MethodName}Invoker" / "{ClassName}Injector"
    // in the SAME namespace as the target type, so these mimic source-generated types.

    public class LookupTarget
    {
        public HttpContext WebSocketHttpContext { get; set; }
        public WebSocket WebSocketClient { get; set; }

        public string Hello(string name) => "real:" + name;

        public int Boom(int x) => x + 1;
    }

    public class LookupTarget_HelloInvoker : IMethodInvoker
    {
        public object Invoke(object instance, object[] args) => "gen:" + args[0];
    }

    /// <summary>Generated-style invoker whose constructor fails -> factory falls back to reflection.</summary>
    public class LookupTarget_BoomInvoker : IMethodInvoker
    {
        public LookupTarget_BoomInvoker()
        {
            throw new InvalidOperationException("generated invoker refuses to construct");
        }

        public object Invoke(object instance, object[] args) => null;
    }

    public class LookupTargetInjector : IEndpointInjector
    {
        public void Inject(object instance, HttpContext httpContext, WebSocket webSocket)
        {
            if (instance is LookupTarget target)
            {
                target.WebSocketHttpContext = httpContext;
                target.WebSocketClient = webSocket;
            }
        }
    }

    public class BoomTarget
    {
    }

    /// <summary>Generated-style injector whose constructor fails -> factory falls back to reflection.</summary>
    public class BoomTargetInjector : IEndpointInjector
    {
        public BoomTargetInjector()
        {
            throw new InvalidOperationException("generated injector refuses to construct");
        }

        public void Inject(object instance, HttpContext httpContext, WebSocket webSocket)
        {
        }
    }

    /// <summary>
    /// Line-coverage tests for the source-generator lookup branches of
    /// MethodInvokerFactory and EndpointInjectorFactory.
    /// </summary>
    public class InjectorCovTests
    {
        private static MethodInfo Method(string name) => typeof(LookupTarget).GetMethod(name);

        [Fact]
        public void MethodInvoker_GeneratedInvokerFound_IsUsed()
        {
            var factory = new MethodInvokerFactory();

            var invoker = factory.GetOrCreateInvoker(Method(nameof(LookupTarget.Hello)));

            Assert.IsType<LookupTarget_HelloInvoker>(invoker);
            Assert.Equal("gen:world", invoker.Invoke(new LookupTarget(), new object[] { "world" }));
        }

        [Fact]
        public void MethodInvoker_GeneratedCtorThrows_FallsBackToReflection()
        {
            var factory = new MethodInvokerFactory();

            var invoker = factory.GetOrCreateInvoker(Method(nameof(LookupTarget.Boom)));

            Assert.IsType<ReflectionMethodInvoker>(invoker);
            Assert.Equal(5, invoker.Invoke(new LookupTarget(), new object[] { 4 }));
        }

        [Fact]
        public void MethodInvoker_MethodWithoutDeclaringType_FallsBackToReflection()
        {
            var dm = new DynamicMethod("Orphan", typeof(int), new[] { typeof(int) });
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ret);
            Assert.Null(dm.DeclaringType);

            var factory = new MethodInvokerFactory();
            var invoker = factory.GetOrCreateInvoker(dm);

            Assert.IsType<ReflectionMethodInvoker>(invoker);
            Assert.Equal(42, invoker.Invoke(null, new object[] { 42 }));
        }

        [Fact]
        public void MethodInvoker_RemoveCache_EvictsSingleMethod()
        {
            var factory = new MethodInvokerFactory();
            var hello1 = factory.GetOrCreateInvoker(Method(nameof(LookupTarget.Hello)));
            var boom1 = factory.GetOrCreateInvoker(Method(nameof(LookupTarget.Boom)));

            factory.RemoveCache(Method(nameof(LookupTarget.Hello)));

            Assert.NotSame(hello1, factory.GetOrCreateInvoker(Method(nameof(LookupTarget.Hello))));
            Assert.Same(boom1, factory.GetOrCreateInvoker(Method(nameof(LookupTarget.Boom))));
        }

        [Fact]
        public void EndpointInjector_GeneratedInjectorFound_IsUsed()
        {
            var factory = new EndpointInjectorFactory(new WebSocketRouteOption());

            var injector = factory.GetOrCreateInjector(typeof(LookupTarget));

            Assert.IsType<LookupTargetInjector>(injector);

            var target = new LookupTarget();
            var ctx = new DefaultHttpContext();
            var socket = new TestWebSocket();
            injector.Inject(target, ctx, socket);
            Assert.Same(ctx, target.WebSocketHttpContext);
            Assert.Same(socket, target.WebSocketClient);
        }

        [Fact]
        public void EndpointInjector_GeneratedCtorThrows_FallsBackToReflection()
        {
            var factory = new EndpointInjectorFactory(new WebSocketRouteOption());

            var injector = factory.GetOrCreateInjector(typeof(BoomTarget));

            Assert.IsType<ReflectionEndpointInjector>(injector);
            injector.Inject(new BoomTarget(), new DefaultHttpContext(), new TestWebSocket());
        }
    }
}
