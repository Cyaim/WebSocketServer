using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.DependencyInjection;

namespace Cyaim.WebSocketServer.Tests.NoPublicCtorControllers
{
    /// <summary>
    /// A base class shared by endpoint classes. Its primary constructor is emitted as protected
    /// because the type is abstract, so the type has no public constructors at all — which is what
    /// used to crash discovery.
    /// </summary>
    public abstract class EndpointBase(string label)
    {
        protected string Label { get; } = label;
    }

    /// <summary>A type whose only constructor is private, e.g. a singleton-style helper.</summary>
    public sealed class PrivateCtorHelper
    {
        private PrivateCtorHelper()
        {
        }

        public static PrivateCtorHelper Instance { get; } = new PrivateCtorHelper();
    }

    /// <summary>A static holder, which reflection also reports as having no instance constructors.</summary>
    public static class StaticHelper
    {
        public static string Name => "helper";
    }

    public class NoCtorController(string label) : EndpointBase(label)
    {
        public NoCtorController() : this("default")
        {
        }

        [WebSocket]
        public string Ping() => "pong-" + Label;
    }
}

namespace Cyaim.WebSocketServer.Tests
{
    using Cyaim.WebSocketServer.Tests.NoPublicCtorControllers;

    /// <summary>
    /// Regression test: a scanned namespace containing a type with no public constructor must not
    /// take the host down at startup.
    /// </summary>
    /// <remarks>
    /// Discovery picked the constructor with the most parameters for every scanned type by taking
    /// <c>Max()</c> over the public constructors. An abstract base class, a private constructor or
    /// a static helper makes that sequence empty, and <c>Max()</c> throws
    /// <c>InvalidOperationException("Sequence contains no elements")</c> out of
    /// <c>AddWebSocketServer</c> — before any log line that would explain it. Such a type can never
    /// be instantiated as an endpoint host, so the fix is to skip it rather than to reject it.
    /// </remarks>
    public class EndpointDiscoveryNoPublicCtorTests
    {
        private static WebSocketRouteOption Discover()
        {
            var services = new ServiceCollection();
            WebSocketRouteOption captured = null;

            services.ConfigureWebSocketRoute(null, o =>
            {
                o.ApplicationServiceCollection = services;
                o.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = (context, logger, options) => Task.CompletedTask
                };
                o.WatchAssemblyPath = typeof(NoCtorController).Assembly.Location;
                o.WatchAssemblyNamespacePrefix = "Cyaim.WebSocketServer.Tests.NoPublicCtorControllers";
                captured = o;
            });

            return captured;
        }

        [Fact]
        public void Discovery_survives_a_namespace_containing_types_without_public_constructors()
        {
            var options = Discover();

            Assert.NotNull(options);
            Assert.Contains(options.WatchAssemblyContext.WatchEndPoint, e => e.MethodPath == "noctor.ping");
        }

        [Fact]
        public void Types_without_a_public_constructor_are_absent_rather_than_broken()
        {
            var options = Discover();
            var max = options.WatchAssemblyContext.MaxConstructorParameters;

            // Discovery loads the assembly with Assembly.LoadFile, which produces a second set of
            // Type identities: typeof(NoCtorController) is NOT the key in this dictionary. Matching
            // on FullName is what actually inspects the scanned types rather than passing
            // vacuously against types that could never be there.
            // 发现流程用 Assembly.LoadFile 加载副本，typeof(X) 与字典里的键不是同一个 Type 标识，
            // 必须按全名匹配，否则断言只是"空过"。
            static bool Has(Dictionary<Type, ConstructorParameter> map, Type type) =>
                map.Keys.Any(k => k.FullName == type.FullName);

            Assert.False(Has(max, typeof(EndpointBase)));
            Assert.False(Has(max, typeof(PrivateCtorHelper)));

            // The real endpoint class still resolves to its widest constructor.
            var entry = max.FirstOrDefault(kv => kv.Key.FullName == typeof(NoCtorController).FullName);
            Assert.NotNull(entry.Key);
            Assert.Equal(1, entry.Value.ParameterInfos.Length);
        }
    }
}
