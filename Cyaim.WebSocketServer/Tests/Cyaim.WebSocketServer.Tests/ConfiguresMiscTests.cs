using System.Reflection;
using Cyaim.WebSocketServer.Examples;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    public class BandwidthLimitPolicyExtensionsTests : IDisposable
    {
        private readonly string _tempDir;

        public BandwidthLimitPolicyExtensionsTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "cyaim-policy-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        private string WriteJson(string content)
        {
            var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, content);
            return path;
        }

        #region LoadFromConfiguration

        [Fact]
        public void LoadFromConfiguration_BindsSection()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["BandwidthLimitPolicy:Enabled"] = "true",
                    ["BandwidthLimitPolicy:GlobalChannelBandwidthLimit:/ws"] = "1048576",
                    ["BandwidthLimitPolicy:ChannelEnableAverageBandwidth:/ws"] = "true",
                    ["BandwidthLimitPolicy:EndPointMaxBandwidthLimit:user.get"] = "2048",
                })
                .Build();
            var policy = new BandwidthLimitPolicy();

            policy.LoadFromConfiguration(configuration);

            Assert.True(policy.Enabled);
            Assert.Equal(1048576, policy.GlobalChannelBandwidthLimit["/ws"]);
            Assert.True(policy.ChannelEnableAverageBandwidth["/ws"]);
            Assert.Equal(2048, policy.EndPointMaxBandwidthLimit["user.get"]);
        }

        [Fact]
        public void LoadFromConfiguration_CustomSectionName()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Custom:Enabled"] = "true",
                })
                .Build();
            var policy = new BandwidthLimitPolicy();

            policy.LoadFromConfiguration(configuration, "Custom");

            Assert.True(policy.Enabled);
        }

        [Fact]
        public void LoadFromConfiguration_MissingSection_NoOp()
        {
            var configuration = new ConfigurationBuilder().Build();
            var policy = new BandwidthLimitPolicy { Enabled = true };

            policy.LoadFromConfiguration(configuration);

            Assert.True(policy.Enabled);
        }

        [Fact]
        public void LoadFromConfiguration_NullPolicyOrConfiguration_NoThrow()
        {
            var configuration = new ConfigurationBuilder().Build();

            ((BandwidthLimitPolicy)null).LoadFromConfiguration(configuration);
            new BandwidthLimitPolicy().LoadFromConfiguration(null);
        }

        #endregion

        #region LoadFromJsonFile

        [Fact]
        public void LoadFromJsonFile_ValidFile_LoadsValues()
        {
            var path = WriteJson(@"{
                ""BandwidthLimitPolicy"": {
                    ""Enabled"": true,
                    ""GlobalChannelBandwidthLimit"": { ""/ws"": 1000 },
                    ""ChannelMaxBandwidthLimit"": { ""/ws"": 500 },
                    ""EndPointMinBandwidthGuarantee"": { ""a.b"": 10 }
                }
            }");
            var policy = new BandwidthLimitPolicy();

            policy.LoadFromJsonFile(path);

            Assert.True(policy.Enabled);
            Assert.Equal(1000, policy.GlobalChannelBandwidthLimit["/ws"]);
            Assert.Equal(500, policy.ChannelMaxBandwidthLimit["/ws"]);
            Assert.Equal(10, policy.EndPointMinBandwidthGuarantee["a.b"]);
            // Dictionaries absent from the file are reset to empty, not null
            Assert.NotNull(policy.ChannelMinBandwidthGuarantee);
            Assert.Empty(policy.ChannelMinBandwidthGuarantee);
        }

        [Fact]
        public void LoadFromJsonFile_MissingSection_NoOp()
        {
            var path = WriteJson(@"{ ""Other"": { ""Enabled"": true } }");
            var policy = new BandwidthLimitPolicy();

            policy.LoadFromJsonFile(path);

            Assert.False(policy.Enabled);
        }

        [Fact]
        public void LoadFromJsonFile_CustomSectionName()
        {
            var path = WriteJson(@"{ ""MyPolicy"": { ""Enabled"": true } }");
            var policy = new BandwidthLimitPolicy();

            policy.LoadFromJsonFile(path, "MyPolicy");

            Assert.True(policy.Enabled);
        }

        [Fact]
        public void LoadFromJsonFile_InvalidJson_ThrowsInvalidOperation()
        {
            var path = WriteJson("{ not json !!!");

            var ex = Assert.Throws<InvalidOperationException>(() => new BandwidthLimitPolicy().LoadFromJsonFile(path));
            Assert.Contains(path, ex.Message);
        }

        [Fact]
        public void LoadFromJsonFile_MissingFileOrNullArgs_NoOp()
        {
            var policy = new BandwidthLimitPolicy { Enabled = true };

            policy.LoadFromJsonFile(Path.Combine(_tempDir, "missing.json"));
            policy.LoadFromJsonFile(null);
            ((BandwidthLimitPolicy)null).LoadFromJsonFile("whatever.json");

            Assert.True(policy.Enabled);
        }

        #endregion
    }

    public class ConfiguresDataClassTests
    {
        [Fact]
        public void WebSocketEndPoint_Properties_RoundTrip()
        {
            var method = typeof(ConfiguresDataClassTests).GetMethod(nameof(WebSocketEndPoint_Properties_RoundTrip));
            var endpoint = new WebSocketEndPoint
            {
                Controller = "TestController",
                Action = "Do",
                MethodPath = "test.do",
                Methods = new[] { "Do" },
                MethodInfo = method,
                Class = typeof(ConfiguresDataClassTests)
            };

            Assert.Equal("TestController", endpoint.Controller);
            Assert.Equal("Do", endpoint.Action);
            Assert.Equal("test.do", endpoint.MethodPath);
            Assert.Equal(new[] { "Do" }, endpoint.Methods);
            Assert.Same(method, endpoint.MethodInfo);
            Assert.Same(typeof(ConfiguresDataClassTests), endpoint.Class);
        }

        [Fact]
        public void ConstructorParameter_Properties_RoundTrip()
        {
            var ctor = typeof(object).GetConstructors()[0];
            var parameter = new ConstructorParameter
            {
                ConstructorInfo = ctor,
                ParameterInfos = ctor.GetParameters()
            };

            Assert.Same(ctor, parameter.ConstructorInfo);
            Assert.Empty(parameter.ParameterInfos);
        }

        [Fact]
        public void ClusterOption_Defaults()
        {
            var option = new ClusterOption();

            Assert.Equal("/cluster", option.ChannelName);
            Assert.Null(option.Nodes);
            Assert.Equal(ServiceLevel.Master, option.NodeLevel);
            Assert.False(option.IsEnableLoadBalance);
            Assert.Equal("ws", option.TransportType);
            Assert.Null(option.NodeId);
            Assert.Null(option.NodeAddress);
            Assert.Equal(0, option.NodePort);
            Assert.Null(option.RedisConnectionString);
            Assert.Null(option.RabbitMQConnectionString);
        }

        [Fact]
        public void ClusterOption_Setters_RoundTrip()
        {
            var option = new ClusterOption
            {
                ChannelName = "/c",
                Nodes = new[] { "n1", "n2" },
                NodeLevel = ServiceLevel.Slave,
                IsEnableLoadBalance = true,
                TransportType = "redis",
                NodeId = "node-1",
                NodeAddress = "127.0.0.1",
                NodePort = 8080,
                RedisConnectionString = "localhost:6379",
                RabbitMQConnectionString = "amqp://"
            };

            Assert.Equal("/c", option.ChannelName);
            Assert.Equal(2, option.Nodes.Length);
            Assert.Equal(ServiceLevel.Slave, option.NodeLevel);
            Assert.True(option.IsEnableLoadBalance);
            Assert.Equal("redis", option.TransportType);
            Assert.Equal("node-1", option.NodeId);
            Assert.Equal("127.0.0.1", option.NodeAddress);
            Assert.Equal(8080, option.NodePort);
            Assert.Equal("localhost:6379", option.RedisConnectionString);
            Assert.Equal("amqp://", option.RabbitMQConnectionString);
        }

        [Fact]
        public void ServiceLevel_EnumValues()
        {
            Assert.Equal(0, (int)ServiceLevel.Master);
            Assert.Equal(1, (int)ServiceLevel.Slave);
        }
    }

    public class BandwidthLimitPolicyUsageExampleTests
    {
        [Fact]
        public void Example1_CodeConfiguration_RegistersPolicy()
        {
            var services = new ServiceCollection();

            BandwidthLimitPolicyUsageExample.Example1_CodeConfiguration(services);

            using var provider = BuildProvider(services);
            var option = provider.GetRequiredService<WebSocketRouteOption>();
            Assert.NotNull(option.BandwidthLimitPolicy);
            Assert.True(option.BandwidthLimitPolicy.Enabled);
            Assert.Equal(10 * 1024 * 1024, option.BandwidthLimitPolicy.GlobalChannelBandwidthLimit["/ws"]);
            Assert.True(option.WebSocketChannels.ContainsKey("/ws"));
        }

        [Fact]
        public void Example2_ConfigurationFile_Runs()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder().Build();

            BandwidthLimitPolicyUsageExample.Example2_ConfigurationFile(services, configuration);

            using var provider = BuildProvider(services);
            Assert.NotNull(provider.GetRequiredService<WebSocketRouteOption>());
        }

        [Fact]
        public void Example3_JsonFile_LoadsPolicyFromFile()
        {
            var services = new ServiceCollection();
            var path = Path.Combine(Path.GetTempPath(), "cyaim-example3-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, @"{ ""BandwidthLimitPolicy"": { ""Enabled"": true } }");
            try
            {
                BandwidthLimitPolicyUsageExample.Example3_JsonFile(services, path);

                using var provider = BuildProvider(services);
                Assert.True(provider.GetRequiredService<WebSocketRouteOption>().BandwidthLimitPolicy.Enabled);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Example4_DynamicUpdate_UpdatesManagerPolicy()
        {
            var manager = new BandwidthLimitManager(
                NullLogger<BandwidthLimitManager>.Instance,
                new BandwidthLimitPolicy());

            BandwidthLimitPolicyUsageExample.Example4_DynamicUpdate(manager);
        }

        [Fact]
        public void Example5_SimpleSingleConnectionLimit_ConfiguresChannelMax()
        {
            var services = new ServiceCollection();

            BandwidthLimitPolicyUsageExample.Example5_SimpleSingleConnectionLimit(services);

            using var provider = BuildProvider(services);
            var option = provider.GetRequiredService<WebSocketRouteOption>();
            Assert.Equal(2 * 1024 * 1024, option.BandwidthLimitPolicy.ChannelMaxBandwidthLimit["/ws"]);
        }

        [Fact]
        public void Example6_AverageBandwidthDistribution_ConfiguresAveraging()
        {
            var services = new ServiceCollection();

            BandwidthLimitPolicyUsageExample.Example6_AverageBandwidthDistribution(services);

            using var provider = BuildProvider(services);
            var option = provider.GetRequiredService<WebSocketRouteOption>();
            Assert.True(option.BandwidthLimitPolicy.ChannelEnableAverageBandwidth["/ws"]);
        }

        private static ServiceProvider BuildProvider(IServiceCollection services)
        {
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(new MvcTestSupport.StubLifetime());
            return services.BuildServiceProvider();
        }
    }

    public class I18nTextTests
    {
        [Fact]
        public void AllPublicStaticStringFields_AreNonEmpty()
        {
            var fields = typeof(I18nText)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(string));

            Assert.NotEmpty(fields);
            foreach (var field in fields)
            {
                var value = (string)field.GetValue(null);
                Assert.False(string.IsNullOrEmpty(value), $"I18nText.{field.Name} must not be null or empty.");
            }
        }

        [Fact]
        public void ResourcePathDefaults()
        {
            Assert.Equal("i18n", I18nText.I18nResourcePath);
            Assert.Equal(".json", I18nText.I18nResourceFileSuffix);
        }

        [Fact]
        public void UpdateI18nText_NonexistentFile_IsNoOp()
        {
            var before = I18nText.ConnectionEntry_Connected;

            I18nText.UpdateI18nText(Path.Combine(Path.GetTempPath(), "no-such-i18n-" + Guid.NewGuid().ToString("N") + ".json"));

            Assert.Equal(before, I18nText.ConnectionEntry_Connected);
        }

        [Fact]
        public void UpdateI18nText_CultureWithoutResourceFile_IsNoOp()
        {
            var before = I18nText.ConnectionEntry_Connected;

            I18nText.UpdateI18nText(System.Globalization.CultureInfo.InvariantCulture);

            Assert.Equal(before, I18nText.ConnectionEntry_Connected);
        }

        [Fact]
        public void InteractiveTextTemplate_FormatsFourArguments()
        {
            string formatted = string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, "1.2.3.4", 80, "conn-1", "hello");

            Assert.Contains("1.2.3.4", formatted);
            Assert.Contains("conn-1", formatted);
            Assert.Contains("hello", formatted);
        }
    }

    public class HandlersMiscTests
    {
        [Fact]
        public void WebSocketHandlerMetadata_Properties_RoundTrip()
        {
            var metadata = new WebSocketHandlerMetadata
            {
                Describe = "d",
                CanHandleBinary = true,
                CanHandleText = false
            };

            Assert.Equal("d", metadata.Describe);
            Assert.True(metadata.CanHandleBinary);
            Assert.False(metadata.CanHandleText);
        }

        [Fact]
        public void MvcChannelHandler_Metadata_AllowsTextAndBinary()
        {
            var handler = new MvcChannelHandler();

            Assert.NotNull(handler.Metadata);
            Assert.True(handler.Metadata.CanHandleText);
            Assert.True(handler.Metadata.CanHandleBinary);
            Assert.False(string.IsNullOrEmpty(handler.Metadata.Describe));
        }

        [Fact]
        public void MvcChannelHandler_DefaultBufferSizes()
        {
            var handler = new MvcChannelHandler();

            Assert.Equal(4 * 1024, handler.ReceiveTextBufferSize);
            Assert.Equal(4 * 1024, handler.ReceiveBinaryBufferSize);
            Assert.Equal(4 * 1024, handler.SendTextBufferSize);
            Assert.Equal(4 * 1024, handler.SendBinaryBufferSize);
            Assert.Null(handler.SubProtocol);
            Assert.Equal(TimeSpan.FromSeconds(10), handler.ResponseSendTimeout);
        }

        [Fact]
        public void MvcChannelHandler_CustomBufferSizes()
        {
            var handler = new MvcChannelHandler(1024, 2048);

            Assert.Equal(1024, handler.ReceiveTextBufferSize);
            Assert.Equal(1024, handler.ReceiveBinaryBufferSize);
            Assert.Equal(2048, handler.SendTextBufferSize);
            Assert.Equal(2048, handler.SendBinaryBufferSize);
        }

        [Fact]
        public void MvcResponseSchemeException_DefaultCtor()
        {
            var ex = new MvcResponseSchemeException();

            Assert.Null(ex.Msg);
            Assert.Equal(0, ex.Status);
            Assert.Null(ex.Id);
            Assert.Null(ex.Target);
        }

        [Fact]
        public void MvcResponseSchemeException_InnerExceptionCtor()
        {
            var inner = new InvalidOperationException("inner");

            var ex = new MvcResponseSchemeException("outer", inner)
            {
                Status = 1,
                RequestTime = 10,
                CompleteTime = 20
            };

            Assert.Equal("outer", ex.Message);
            Assert.Equal("outer", ex.Msg);
            Assert.Same(inner, ex.InnerException);
            Assert.Equal(1, ex.Status);
            Assert.Equal(10, ex.RequestTime);
            Assert.Equal(20, ex.CompleteTime);
        }

        [Fact]
        public void MvcResponseScheme_Properties_RoundTrip()
        {
            var scheme = new MvcResponseScheme
            {
                Status = 2,
                Msg = "m",
                RequestTime = 1,
                CompleteTime = 2,
                Id = "i",
                Target = "t",
                Body = 42
            };

            Assert.Equal(2, scheme.Status);
            Assert.Equal("m", scheme.Msg);
            Assert.Equal(1, scheme.RequestTime);
            Assert.Equal(2, scheme.CompleteTime);
            Assert.Equal("i", scheme.Id);
            Assert.Equal("t", scheme.Target);
            Assert.Equal(42, scheme.Body);
        }
    }
}
