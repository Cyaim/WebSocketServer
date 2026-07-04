using System.Net.WebSockets;
using System.Reflection;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Injectors;
using Microsoft.AspNetCore.Http;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// xunit port of the meaningful assertions from
    /// Tests/Cyaim.WebSocketServer.Tests.Injector/EndpointInjectorFactoryTests.cs.
    /// No source generator is referenced by this test project, so the factories
    /// fall back to the reflection-based implementations.
    /// </summary>
    public class InjectorFactoryTests
    {
        public class TestEndpoint
        {
            public HttpContext WebSocketHttpContext { get; set; }
            public WebSocket WebSocketClient { get; set; }

            public string Echo(string message) => $"Echo: {message}";

            public int Add(int a, int b) => a + b;

            public void VoidMethod(string message)
            {
                LastVoidMessage = message;
            }

            public string LastVoidMessage { get; private set; }

            public async Task<int> AsyncAdd(int a, int b)
            {
                await Task.Delay(1);
                return a + b;
            }

            public Task<string> TaskEcho(string message) => Task.FromResult($"Echo: {message}");
        }

        public class CustomNamesEndpoint
        {
            public HttpContext Ctx { get; set; }
            public WebSocket Sock { get; set; }
        }

        public class NoInjectablePropertiesEndpoint
        {
            public int Unrelated { get; set; }
        }

        #region EndpointInjectorFactory

        [Fact]
        public void Injector_SetsWebSocketHttpContextAndWebSocketClient()
        {
            var factory = new EndpointInjectorFactory(new WebSocketRouteOption());
            var injector = factory.GetOrCreateInjector(typeof(TestEndpoint));

            var instance = new TestEndpoint();
            var httpContext = new DefaultHttpContext();
            var socket = new TestWebSocket();

            injector.Inject(instance, httpContext, socket);

            Assert.Same(httpContext, instance.WebSocketHttpContext);
            Assert.Same(socket, instance.WebSocketClient);
        }

        [Fact]
        public void Injector_FallsBackToReflectionInjector_WhenNoGeneratedInjectorExists()
        {
            var factory = new EndpointInjectorFactory(new WebSocketRouteOption());

            var injector = factory.GetOrCreateInjector(typeof(TestEndpoint));

            Assert.IsType<ReflectionEndpointInjector>(injector);
        }

        [Fact]
        public void Injector_HonorsCustomPropertyNames()
        {
            var options = new WebSocketRouteOption
            {
                InjectionHttpContextPropertyName = "Ctx",
                InjectionWebSocketClientPropertyName = "Sock"
            };
            var factory = new EndpointInjectorFactory(options);
            var injector = factory.GetOrCreateInjector(typeof(CustomNamesEndpoint));

            var instance = new CustomNamesEndpoint();
            var httpContext = new DefaultHttpContext();
            var socket = new TestWebSocket();

            injector.Inject(instance, httpContext, socket);

            Assert.Same(httpContext, instance.Ctx);
            Assert.Same(socket, instance.Sock);
        }

        [Fact]
        public void Injector_MissingProperties_DoesNotThrow()
        {
            var factory = new EndpointInjectorFactory(new WebSocketRouteOption());
            var injector = factory.GetOrCreateInjector(typeof(NoInjectablePropertiesEndpoint));

            var instance = new NoInjectablePropertiesEndpoint();
            injector.Inject(instance, new DefaultHttpContext(), new TestWebSocket());

            Assert.Equal(0, instance.Unrelated);
        }

        [Fact]
        public void Injector_NullInstance_DoesNotThrow()
        {
            var factory = new EndpointInjectorFactory(new WebSocketRouteOption());
            var injector = factory.GetOrCreateInjector(typeof(TestEndpoint));

            injector.Inject(null, new DefaultHttpContext(), new TestWebSocket());
        }

        [Fact]
        public void Injector_IsCachedPerType()
        {
            var factory = new EndpointInjectorFactory(new WebSocketRouteOption());

            var first = factory.GetOrCreateInjector(typeof(TestEndpoint));
            var second = factory.GetOrCreateInjector(typeof(TestEndpoint));

            Assert.Same(first, second);
        }

        [Fact]
        public void Injector_ClearCache_CreatesNewInstance()
        {
            var factory = new EndpointInjectorFactory(new WebSocketRouteOption());
            var first = factory.GetOrCreateInjector(typeof(TestEndpoint));

            factory.ClearCache();
            var second = factory.GetOrCreateInjector(typeof(TestEndpoint));

            Assert.NotSame(first, second);
        }

        [Fact]
        public void Injector_RemoveCache_EvictsSingleType()
        {
            var factory = new EndpointInjectorFactory(new WebSocketRouteOption());
            var a1 = factory.GetOrCreateInjector(typeof(TestEndpoint));
            var b1 = factory.GetOrCreateInjector(typeof(CustomNamesEndpoint));

            factory.RemoveCache(typeof(TestEndpoint));

            Assert.NotSame(a1, factory.GetOrCreateInjector(typeof(TestEndpoint)));
            Assert.Same(b1, factory.GetOrCreateInjector(typeof(CustomNamesEndpoint)));
        }

        [Fact]
        public void InjectorFactory_NullOptions_UsesDefaultPropertyNames()
        {
            var factory = new EndpointInjectorFactory(null);
            var injector = factory.GetOrCreateInjector(typeof(TestEndpoint));

            var instance = new TestEndpoint();
            var httpContext = new DefaultHttpContext();
            injector.Inject(instance, httpContext, null);

            Assert.Same(httpContext, instance.WebSocketHttpContext);
        }

        #endregion

        #region MethodInvokerFactory

        private static MethodInfo Method(string name) => typeof(TestEndpoint).GetMethod(name);

        [Fact]
        public void Invoker_SyncMethodWithReturnValue()
        {
            var factory = new MethodInvokerFactory();
            var invoker = factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.Echo)));

            object result = invoker.Invoke(new TestEndpoint(), new object[] { "Hello" });

            Assert.Equal("Echo: Hello", result);
        }

        [Fact]
        public void Invoker_SyncMethodWithValueTypeArgs()
        {
            var factory = new MethodInvokerFactory();
            var invoker = factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.Add)));

            object result = invoker.Invoke(new TestEndpoint(), new object[] { 5, 3 });

            Assert.Equal(8, result);
        }

        [Fact]
        public void Invoker_VoidMethod_ReturnsNull_AndExecutes()
        {
            var factory = new MethodInvokerFactory();
            var invoker = factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.VoidMethod)));
            var instance = new TestEndpoint();

            object result = invoker.Invoke(instance, new object[] { "ping" });

            Assert.Null(result);
            Assert.Equal("ping", instance.LastVoidMessage);
        }

        [Fact]
        public async Task Invoker_AsyncMethod_ReturnsAwaitableTaskWithResult()
        {
            var factory = new MethodInvokerFactory();
            var invoker = factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.AsyncAdd)));

            object result = invoker.Invoke(new TestEndpoint(), new object[] { 5, 3 });

            var task = Assert.IsAssignableFrom<Task<int>>(result);
            Assert.Equal(8, await task);
        }

        [Fact]
        public async Task Invoker_TaskReturningMethod()
        {
            var factory = new MethodInvokerFactory();
            var invoker = factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.TaskEcho)));

            object result = invoker.Invoke(new TestEndpoint(), new object[] { "x" });

            var task = Assert.IsAssignableFrom<Task<string>>(result);
            Assert.Equal("Echo: x", await task);
        }

        [Fact]
        public void Invoker_FallsBackToReflectionInvoker_WhenNoGeneratedInvokerExists()
        {
            var factory = new MethodInvokerFactory();

            var invoker = factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.Echo)));

            Assert.IsType<ReflectionMethodInvoker>(invoker);
        }

        [Fact]
        public void Invoker_IsCachedPerMethod()
        {
            var factory = new MethodInvokerFactory();

            var first = factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.Add)));
            var second = factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.Add)));
            var other = factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.Echo)));

            Assert.Same(first, second);
            Assert.NotSame(first, other);
        }

        [Fact]
        public void Invoker_ClearCache_CreatesNewInstance()
        {
            var factory = new MethodInvokerFactory();
            var first = factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.Add)));

            factory.ClearCache();

            Assert.NotSame(first, factory.GetOrCreateInvoker(Method(nameof(TestEndpoint.Add))));
        }

        [Fact]
        public void ReflectionMethodInvoker_NullMethod_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ReflectionMethodInvoker(null));
        }

        [Fact]
        public void ReflectionEndpointInjector_NullType_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ReflectionEndpointInjector(null, "A", "B"));
        }

        #endregion
    }
}
