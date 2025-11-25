using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace Cyaim.WebSocketServer.Examples
{
    /// <summary>
    /// 带宽限速策略使用示例
    /// </summary>
    public static class BandwidthLimitPolicyUsageExample
    {
        /// <summary>
        /// 示例1：通过代码配置限速策略
        /// </summary>
        public static void Example1_CodeConfiguration(IServiceCollection services)
        {
            services.ConfigureWebSocketRoute(x =>
            {
                var mvcHandler = new MvcChannelHandler();
                
                x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
                {
                    { "/ws", mvcHandler.ConnectionEntry }
                };
                
                // 配置限速策略
                x.BandwidthLimitPolicy = new BandwidthLimitPolicy
                {
                    Enabled = true,
                    
                    // 全局服务级别：每个通道的总限速（10MB/s）
                    GlobalChannelBandwidthLimit = new Dictionary<string, long>
                    {
                        { "/ws", 10 * 1024 * 1024 }
                    },
                    
                    // 通道级别（单个连接）：最低带宽保障（1MB/s）
                    ChannelMinBandwidthGuarantee = new Dictionary<string, long>
                    {
                        { "/ws", 1024 * 1024 }
                    },
                    
                    // 通道级别（单个连接）：最高带宽限制（5MB/s）
                    ChannelMaxBandwidthLimit = new Dictionary<string, long>
                    {
                        { "/ws", 5 * 1024 * 1024 }
                    },
                    
                    // 通道级别（多个连接）：启用平均分配带宽
                    ChannelEnableAverageBandwidth = new Dictionary<string, bool>
                    {
                        { "/ws", true }
                    },
                    
                    // 通道级别（多个连接）：每个连接的最低带宽保障（512KB/s）
                    ChannelConnectionMinBandwidthGuarantee = new Dictionary<string, long>
                    {
                        { "/ws", 512 * 1024 }
                    },
                    
                    // 通道级别（多个连接）：每个连接的最高带宽限制（2MB/s）
                    ChannelConnectionMaxBandwidthLimit = new Dictionary<string, long>
                    {
                        { "/ws", 2 * 1024 * 1024 }
                    },
                    
                    // WebSocket端点级别：端点最高限速（1MB/s）
                    EndPointMaxBandwidthLimit = new Dictionary<string, long>
                    {
                        { "user.getuser", 1024 * 1024 },
                        { "file.upload", 5 * 1024 * 1024 }
                    },
                    
                    // WebSocket端点级别：端点最低带宽保障（256KB/s）
                    EndPointMinBandwidthGuarantee = new Dictionary<string, long>
                    {
                        { "user.getuser", 256 * 1024 }
                    }
                };
                
                x.ApplicationServiceCollection = services;
            });
        }

        /// <summary>
        /// 示例2：从配置文件加载限速策略
        /// </summary>
        public static void Example2_ConfigurationFile(IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            services.ConfigureWebSocketRoute(x =>
            {
                var mvcHandler = new MvcChannelHandler();
                
                x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
                {
                    { "/ws", mvcHandler.ConnectionEntry }
                };
                
                // 从IConfiguration加载限速策略（自动加载，无需手动配置）
                // 系统会自动从 appsettings.json 中的 "BandwidthLimitPolicy" 节点加载
                
                // 或者手动加载：
                // x.BandwidthLimitPolicy = new BandwidthLimitPolicy();
                // x.BandwidthLimitPolicy.LoadFromConfiguration(configuration);
                
                x.ApplicationServiceCollection = services;
            });
        }

        /// <summary>
        /// 示例3：从JSON文件加载限速策略
        /// </summary>
        public static void Example3_JsonFile(IServiceCollection services, string jsonFilePath)
        {
            services.ConfigureWebSocketRoute(x =>
            {
                var mvcHandler = new MvcChannelHandler();
                
                x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
                {
                    { "/ws", mvcHandler.ConnectionEntry }
                };
                
                // 从JSON文件加载限速策略
                var policy = new BandwidthLimitPolicy();
                policy.LoadFromJsonFile(jsonFilePath);
                x.BandwidthLimitPolicy = policy;
                
                x.ApplicationServiceCollection = services;
            });
        }

        /// <summary>
        /// 示例4：动态更新限速策略
        /// </summary>
        public static void Example4_DynamicUpdate(Cyaim.WebSocketServer.Infrastructure.BandwidthLimitManager manager)
        {
            // 创建新的策略配置
            var newPolicy = new BandwidthLimitPolicy
            {
                Enabled = true,
                GlobalChannelBandwidthLimit = new Dictionary<string, long>
                {
                    { "/ws", 20 * 1024 * 1024 } // 更新为20MB/s
                }
            };
            
            // 更新策略
            manager.UpdatePolicy(newPolicy);
        }

        /// <summary>
        /// 示例5：简单的单连接限速
        /// </summary>
        public static void Example5_SimpleSingleConnectionLimit(IServiceCollection services)
        {
            services.ConfigureWebSocketRoute(x =>
            {
                var mvcHandler = new MvcChannelHandler();
                
                x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
                {
                    { "/ws", mvcHandler.ConnectionEntry }
                };
                
                // 简单的单连接限速：每个连接最多2MB/s
                x.BandwidthLimitPolicy = new BandwidthLimitPolicy
                {
                    Enabled = true,
                    ChannelMaxBandwidthLimit = new Dictionary<string, long>
                    {
                        { "/ws", 2 * 1024 * 1024 }
                    }
                };
                
                x.ApplicationServiceCollection = services;
            });
        }

        /// <summary>
        /// 示例6：多连接平均分配带宽
        /// </summary>
        public static void Example6_AverageBandwidthDistribution(IServiceCollection services)
        {
            services.ConfigureWebSocketRoute(x =>
            {
                var mvcHandler = new MvcChannelHandler();
                
                x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
                {
                    { "/ws", mvcHandler.ConnectionEntry }
                };
                
                // 多连接时平均分配带宽
                // 通道总带宽10MB/s，如果有5个连接，每个连接将获得约2MB/s
                x.BandwidthLimitPolicy = new BandwidthLimitPolicy
                {
                    Enabled = true,
                    GlobalChannelBandwidthLimit = new Dictionary<string, long>
                    {
                        { "/ws", 10 * 1024 * 1024 } // 通道总带宽10MB/s
                    },
                    ChannelEnableAverageBandwidth = new Dictionary<string, bool>
                    {
                        { "/ws", true } // 启用平均分配
                    }
                };
                
                x.ApplicationServiceCollection = services;
            });
        }
    }
}

