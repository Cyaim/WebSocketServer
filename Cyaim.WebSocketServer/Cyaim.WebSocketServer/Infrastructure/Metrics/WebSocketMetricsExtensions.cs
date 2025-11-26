using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using System;

namespace Cyaim.WebSocketServer.Infrastructure.Metrics
{
    /// <summary>
    /// Extension methods for configuring OpenTelemetry metrics
    /// 用于配置 OpenTelemetry 指标的扩展方法
    /// </summary>
    public static class WebSocketMetricsExtensions
    {
        /// <summary>
        /// Add WebSocket metrics collection with OpenTelemetry
        /// 添加 WebSocket 指标收集（使用 OpenTelemetry）
        /// </summary>
        /// <param name="services">Service collection / 服务集合</param>
        /// <returns>Service collection / 服务集合</returns>
        public static IServiceCollection AddWebSocketMetrics(this IServiceCollection services)
        {
            // Register metrics collector as singleton / 将指标收集器注册为单例
            services.AddSingleton<WebSocketMetricsCollector>();

            return services;
        }

        /// <summary>
        /// Configure OpenTelemetry metrics with OTLP exporter
        /// 配置 OpenTelemetry 指标（使用 OTLP 导出器）
        /// </summary>
        /// <param name="builder">Meter provider builder / Meter 提供者构建器</param>
        /// <param name="configure">Optional OTLP exporter configuration / 可选的 OTLP 导出器配置</param>
        /// <returns>Meter provider builder / Meter 提供者构建器</returns>
        public static MeterProviderBuilder AddWebSocketMetricsExporter(
            this MeterProviderBuilder builder,
            Action<OpenTelemetry.Exporter.OtlpExporterOptions> configure = null)
        {
            // Add WebSocket metrics meter / 添加 WebSocket 指标 Meter
            var meterProviderBuilder = builder
                .AddMeter("Cyaim.WebSocketServer");

            // Add OTLP exporter / 添加 OTLP 导出器
            // 默认配置可以通过环境变量设置：
            // OTEL_EXPORTER_OTLP_ENDPOINT - OTLP 端点地址（默认：http://localhost:4317 for gRPC, http://localhost:4318 for HTTP）
            // OTEL_EXPORTER_OTLP_PROTOCOL - 协议类型（grpc 或 http/protobuf，默认：grpc）
            // OTEL_EXPORTER_OTLP_HEADERS - 额外的 HTTP 头（格式：key1=value1,key2=value2）
            meterProviderBuilder.AddOtlpExporter(options =>
            {
                configure?.Invoke(options);
            });

            return meterProviderBuilder;
        }
    }
}
