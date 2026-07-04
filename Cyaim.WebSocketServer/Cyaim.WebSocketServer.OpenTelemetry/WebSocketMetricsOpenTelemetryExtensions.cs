using System;
using OpenTelemetry.Metrics;

namespace Cyaim.WebSocketServer.OpenTelemetry
{
    /// <summary>
    /// OpenTelemetry OTLP exporter helpers for Cyaim.WebSocketServer metrics.
    /// Cyaim.WebSocketServer 指标的 OpenTelemetry OTLP 导出扩展。
    /// </summary>
    /// <remarks>
    /// 核心库通过 BCL Meter（名称 "Cyaim.WebSocketServer"）发布指标、不依赖任何 OpenTelemetry 包；
    /// 本可选包提供把该 Meter 接入 OTLP 导出的便捷方法。
    /// The core library publishes metrics via the BCL Meter "Cyaim.WebSocketServer" with no OpenTelemetry
    /// dependency; this optional package wires that meter into an OTLP exporter.
    /// </remarks>
    public static class WebSocketMetricsOpenTelemetryExtensions
    {
        /// <summary>
        /// Add the WebSocket metrics meter and an OTLP exporter to the meter provider.
        /// 将 WebSocket 指标 Meter 与 OTLP 导出器添加到 MeterProvider。
        /// </summary>
        /// <param name="builder">Meter provider builder / Meter 提供者构建器</param>
        /// <param name="configure">Optional OTLP exporter configuration / 可选的 OTLP 导出器配置</param>
        /// <returns>Meter provider builder / Meter 提供者构建器</returns>
        /// <remarks>
        /// 默认端点等可通过环境变量配置：
        /// OTEL_EXPORTER_OTLP_ENDPOINT（默认 gRPC http://localhost:4317 / HTTP http://localhost:4318）、
        /// OTEL_EXPORTER_OTLP_PROTOCOL（grpc 或 http/protobuf）、OTEL_EXPORTER_OTLP_HEADERS。
        /// </remarks>
        public static MeterProviderBuilder AddWebSocketMetricsExporter(
            this MeterProviderBuilder builder,
            Action<global::OpenTelemetry.Exporter.OtlpExporterOptions> configure = null)
        {
            var meterProviderBuilder = builder.AddMeter("Cyaim.WebSocketServer");
            meterProviderBuilder.AddOtlpExporter(options => configure?.Invoke(options));
            return meterProviderBuilder;
        }
    }
}
