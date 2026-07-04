using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Handlers;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Metrics;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Plants an invalid i18n resource file for the current culture BEFORE anything
    /// in the test run touches I18nText: the type's one-time static constructor then
    /// throws internally while reading it, covering the cctor's swallow-all catch.
    /// Field initializers run before the cctor body, so every text keeps its default.
    /// </summary>
    internal static class I18nBadResourcePlanter
    {
        internal static string PlantedFile;

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Plant()
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "i18n");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, CultureInfo.CurrentCulture.Name + ".json");
                if (!File.Exists(file))
                {
                    File.WriteAllText(file, "{ invalid json planted for I18nText cctor catch coverage");
                    PlantedFile = file;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                    {
                        try { File.Delete(file); } catch { }
                    };
                }
            }
            catch
            {
                // Best effort only; never break test discovery.
            }
        }
    }

    /// <summary>
    /// Line-coverage tests for small remaining spots:
    /// I18nText error paths, RequestPipelineMiddlewareExtensions, DataTypes.IsBasicType catch,
    /// WebSocketMetricsExtensions (OpenTelemetry), PriorityListService CIDR edge cases.
    /// </summary>
    [Collection("StaticState")]
    public class MiscCovTests : IDisposable
    {
        private readonly string _tempDir;

        public MiscCovTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "cyaim-misccov-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        #region I18nText

        [Fact]
        public void I18nText_UpdateByCulture_RootedPath_NoResourceFile_NoThrow()
        {
            var original = I18nText.I18nResourcePath;
            try
            {
                I18nText.I18nResourcePath = _tempDir; // rooted
                I18nText.UpdateI18nText(new CultureInfo("fr-FR"));
            }
            finally
            {
                I18nText.I18nResourcePath = original;
            }
        }

        [Fact]
        public void I18nText_UpdateByPath_InvalidJson_Throws()
        {
            var path = Path.Combine(_tempDir, "bad.json");
            File.WriteAllText(path, "{ this is not json");

            Assert.ThrowsAny<Exception>(() => I18nText.UpdateI18nText(path));
        }

        [Fact]
        public void I18nText_UpdateByCulture_InvalidJson_Rethrows()
        {
            var original = I18nText.I18nResourcePath;
            try
            {
                I18nText.I18nResourcePath = _tempDir; // rooted
                File.WriteAllText(Path.Combine(_tempDir, "de-DE.json"), "{ not json");

                Assert.ThrowsAny<Exception>(() => I18nText.UpdateI18nText(new CultureInfo("de-DE")));
            }
            finally
            {
                I18nText.I18nResourcePath = original;
            }
        }

        [Fact]
        public void I18nText_StaticInitializer_SwallowedPlantedBadResource_KeepsDefaults()
        {
            // I18nBadResourcePlanter's module initializer planted an invalid JSON
            // resource for the current culture before I18nText was first touched,
            // so the static constructor hit its catch. All texts must keep defaults.
            Assert.Equal("i18n", I18nText.I18nResourcePath);
            Assert.Equal(".json", I18nText.I18nResourceFileSuffix);
            Assert.Equal("No error specified.", I18nText.WebSocketCloseStatus_Empty);
            Assert.Equal("Connected.", I18nText.ConnectionEntry_Connected);
        }

        #endregion

        #region RequestPipelineMiddlewareExtensions

        [Fact]
        public void AddRequestMiddleware_PipelineItemOverload_AddsToNewAndExistingQueues()
        {
            var pipeline = new ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>>();
            var first = new PipelineItem
            {
                Stage = RequestPipelineStage.Connected,
                Order = 1,
                Item = new DelegateRequestPipeline(_ => Task.CompletedTask)
            };
            var second = new PipelineItem
            {
                Stage = RequestPipelineStage.Connected,
                Order = 2,
                Item = new DelegateRequestPipeline(_ => Task.CompletedTask)
            };

            var returned = pipeline.AddRequestMiddleware(first).AddRequestMiddleware(second);

            Assert.Same(pipeline, returned);
            Assert.True(pipeline.TryGetValue(RequestPipelineStage.Connected, out var queue));
            Assert.Equal(2, queue.Count);
        }

        #endregion

        #region DataTypes

        [Fact]
        public void IsBasicType_NullType_CatchRethrows()
        {
            Assert.Throws<NullReferenceException>(() => ((Type)null).IsBasicType());
        }

        #endregion

        #region WebSocketMetricsExtensions

        [Fact]
        public void AddWebSocketMetrics_RegistersCollector()
        {
            var services = new ServiceCollection();

            var returned = services.AddWebSocketMetrics();

            Assert.Same(services, returned);
            Assert.Contains(services, d => d.ServiceType == typeof(WebSocketMetricsCollector) && d.Lifetime == ServiceLifetime.Singleton);
        }

        [Fact]
        public void AddWebSocketMetricsExporter_ConfiguresOtlpExporter()
        {
            bool configured = false;

            var builder = OpenTelemetry.Sdk.CreateMeterProviderBuilder()
                .AddWebSocketMetricsExporter(options =>
                {
                    configured = true;
                    // Point at a closed local port with a minimal timeout so disposing
                    // the provider (final flush) fails fast instead of waiting.
                    options.Endpoint = new Uri("http://127.0.0.1:9");
                    options.TimeoutMilliseconds = 1;
                });

            Assert.NotNull(builder);

            using (var provider = builder.Build())
            {
                Assert.NotNull(provider);
            }

            Assert.True(configured);
        }

        #endregion

        #region PriorityListService — CIDR edge cases

        private static PriorityListService NewPriorityList()
            => new PriorityListService(NullLogger<PriorityListService>.Instance);

        [Fact]
        public void PriorityList_CidrWithRemainderBits_Matches()
        {
            var service = NewPriorityList();
            Assert.True(service.AddIpToPriorityList("10.16.0.0/12", 7));

            Assert.Equal(7, service.GetIpPriority("10.31.4.5"));
            Assert.Equal(1, service.GetIpPriority("10.64.4.5"));
        }

        [Fact]
        public void PriorityList_CidrPrefixTooLong_CaughtAndIgnored()
        {
            var service = NewPriorityList();
            // /40 on IPv4 makes GetMaskBytes write past the 4-byte mask -> exception
            // swallowed by IsIpInCidrRange's catch -> treated as no match.
            Assert.True(service.AddIpToPriorityList("1.0.0.0/40", 9));

            Assert.Equal(1, service.GetIpPriority("9.9.9.9"));
        }

        [Fact]
        public void PriorityList_IsIpInCidrRange_NullOrEmptyCidr_False()
        {
            var service = NewPriorityList();
            var method = typeof(PriorityListService).GetMethod("IsIpInCidrRange", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var ip = IPAddress.Parse("1.2.3.4");
            Assert.False((bool)method.Invoke(service, new object[] { ip, null }));
            Assert.False((bool)method.Invoke(service, new object[] { ip, "" }));
        }

        #endregion
    }
}
