using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Cyaim.WebSocketServer.Dashboard.Middlewares
{
    /// <summary>
    /// Extension methods for Dashboard middleware
    /// Dashboard 中间件的扩展方法
    /// </summary>
    public static class DashboardMiddlewareExtensions
    {
        /// <summary>
        /// Add Dashboard services / 添加 Dashboard 服务
        /// </summary>
        /// <param name="services">Service collection / 服务集合</param>
        /// <returns>Service collection / 服务集合</returns>
        public static IServiceCollection AddWebSocketDashboard(this IServiceCollection services)
        {
            services.AddSingleton<Services.DashboardStatisticsService>();
            services.AddSingleton<Services.DashboardHelperService>();
            return services;
        }

        /// <summary>
        /// Use Dashboard middleware / 使用 Dashboard 中间件
        /// </summary>
        /// <param name="app">Application builder / 应用程序构建器</param>
        /// <param name="dashboardPath">Dashboard path / Dashboard 路径</param>
        /// <returns>Application builder / 应用程序构建器</returns>
        public static IApplicationBuilder UseWebSocketDashboard(
            this IApplicationBuilder app,
            string dashboardPath = "/dashboard")
        {
            app.UseMiddleware<DashboardMiddleware>(dashboardPath);
            return app;
        }
    }
}

