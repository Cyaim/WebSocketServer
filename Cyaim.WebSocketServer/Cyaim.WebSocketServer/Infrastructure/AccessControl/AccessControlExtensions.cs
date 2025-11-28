using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// Extension methods for access control configuration / 访问控制配置扩展方法
    /// </summary>
    public static class AccessControlExtensions
    {
        /// <summary>
        /// Add access control service / 添加访问控制服务
        /// </summary>
        /// <param name="services">Service collection / 服务集合</param>
        /// <param name="configure">Configuration action / 配置操作</param>
        /// <returns>Service collection / 服务集合</returns>
        public static IServiceCollection AddAccessControl(
            this IServiceCollection services,
            Action<AccessControlPolicy> configure = null)
        {
            var policy = new AccessControlPolicy();
            configure?.Invoke(policy);

            services.AddSingleton(policy);
            services.AddSingleton<AccessControlService>();

            return services;
        }

        /// <summary>
        /// Add access control service from configuration / 从配置添加访问控制服务
        /// </summary>
        /// <param name="services">Service collection / 服务集合</param>
        /// <param name="configuration">Configuration / 配置</param>
        /// <param name="sectionName">Configuration section name (default: "AccessControlPolicy") / 配置节名称（默认："AccessControlPolicy"）</param>
        /// <returns>Service collection / 服务集合</returns>
        public static IServiceCollection AddAccessControl(
            this IServiceCollection services,
            IConfiguration configuration,
            string sectionName = "AccessControlPolicy")
        {
            var policy = new AccessControlPolicy();
            configuration.GetSection(sectionName).Bind(policy);

            services.AddSingleton(policy);
            services.AddSingleton<AccessControlService>();

            return services;
        }

        /// <summary>
        /// Register geographic location provider / 注册地理位置提供者
        /// </summary>
        /// <typeparam name="T">Provider type / 提供者类型</typeparam>
        /// <param name="services">Service collection / 服务集合</param>
        /// <returns>Service collection / 服务集合</returns>
        public static IServiceCollection AddGeoLocationProvider<T>(this IServiceCollection services)
            where T : class, IGeoLocationProvider
        {
            services.AddSingleton<IGeoLocationProvider, T>();
            return services;
        }

        /// <summary>
        /// Add QPS priority policy service / 添加QPS优先级策略服务
        /// </summary>
        /// <param name="services">Service collection / 服务集合</param>
        /// <param name="configure">Configuration action / 配置操作</param>
        /// <returns>Service collection / 服务集合</returns>
        public static IServiceCollection AddQpsPriorityPolicy(
            this IServiceCollection services,
            Action<QpsPriorityPolicy> configure = null)
        {
            var policy = new QpsPriorityPolicy();
            configure?.Invoke(policy);

            services.AddSingleton(policy);
            services.AddSingleton<PriorityListService>();
            services.AddSingleton<QpsPriorityManager>();

            return services;
        }

        /// <summary>
        /// Add QPS priority policy service from configuration / 从配置添加QPS优先级策略服务
        /// </summary>
        /// <param name="services">Service collection / 服务集合</param>
        /// <param name="configuration">Configuration / 配置</param>
        /// <param name="sectionName">Configuration section name (default: "QpsPriorityPolicy") / 配置节名称（默认："QpsPriorityPolicy"）</param>
        /// <returns>Service collection / 服务集合</returns>
        public static IServiceCollection AddQpsPriorityPolicy(
            this IServiceCollection services,
            IConfiguration configuration,
            string sectionName = "QpsPriorityPolicy")
        {
            var policy = new QpsPriorityPolicy();
            configuration.GetSection(sectionName).Bind(policy);

            services.AddSingleton(policy);
            services.AddSingleton<PriorityListService>();
            services.AddSingleton<QpsPriorityManager>();

            return services;
        }
    }
}

