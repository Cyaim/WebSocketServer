using Microsoft.Extensions.DependencyInjection;

namespace Cyaim.WebSocketServer.Infrastructure.Metrics
{
    /// <summary>
    /// Extension methods for WebSocket metrics collection.
    /// WebSocket 指标收集的扩展方法。
    /// </summary>
    /// <remarks>
    /// 指标通过 BCL 的 <see cref="System.Diagnostics.Metrics.Meter"/>（名称 "Cyaim.WebSocketServer"）发布，
    /// 核心库不依赖任何 OpenTelemetry 包。若需 OTLP 导出，请引用可选包
    /// <c>Cyaim.WebSocketServer.OpenTelemetry</c> 并调用其 <c>AddWebSocketMetricsExporter()</c>，
    /// 或在应用侧自行 <c>AddMeter("Cyaim.WebSocketServer")</c> 接入任意导出器。
    /// Metrics are published via the BCL <see cref="System.Diagnostics.Metrics.Meter"/> named
    /// "Cyaim.WebSocketServer"; the core library takes no OpenTelemetry dependency. For OTLP export,
    /// reference the optional Cyaim.WebSocketServer.OpenTelemetry package, or wire up any exporter
    /// yourself with AddMeter("Cyaim.WebSocketServer").
    /// </remarks>
    public static class WebSocketMetricsExtensions
    {
        /// <summary>
        /// Register the WebSocket metrics collector as a singleton.
        /// 将 WebSocket 指标收集器注册为单例。
        /// </summary>
        /// <param name="services">Service collection / 服务集合</param>
        /// <returns>Service collection / 服务集合</returns>
        public static IServiceCollection AddWebSocketMetrics(this IServiceCollection services)
        {
            services.AddSingleton<WebSocketMetricsCollector>();
            return services;
        }
    }
}
