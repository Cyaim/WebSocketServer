using System.Collections.Generic;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// QPS优先级策略配置
    /// 用于在固定带宽下，根据优先级分配带宽资源
    /// </summary>
    public class QpsPriorityPolicy
    {
        /// <summary>
        /// 是否启用QPS优先级策略
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// 全局总带宽限制（字节/秒）
        /// 如果设置，将在此总带宽下进行优先级分配
        /// </summary>
        public long? GlobalBandwidthLimit { get; set; }

        /// <summary>
        /// 优先级配置
        /// Key: 优先级等级（数字越大优先级越高，例如：1=普通，2=VIP，3=超级VIP）
        /// Value: 该优先级分配的带宽比例（0.0-1.0，所有优先级比例之和应小于等于1.0）
        /// </summary>
        public Dictionary<int, double> PriorityBandwidthRatios { get; set; } = new Dictionary<int, double>();

        /// <summary>
        /// 默认优先级（不在任何名单中的用户使用的优先级）
        /// </summary>
        public int DefaultPriority { get; set; } = 1;

        /// <summary>
        /// 默认优先级带宽比例（当不在任何优先级名单中时）
        /// </summary>
        public double DefaultPriorityBandwidthRatio { get; set; } = 0.1; // 默认10%

        /// <summary>
        /// 是否启用动态带宽调整
        /// 启用后，会根据实际连接数动态调整各优先级的带宽分配
        /// </summary>
        public bool EnableDynamicBandwidthAdjustment { get; set; } = true;

        /// <summary>
        /// 最小带宽保障（字节/秒）
        /// 即使优先级较低，也保证每个连接至少能获得此带宽
        /// </summary>
        public long? MinBandwidthGuarantee { get; set; }

        /// <summary>
        /// 通道级别的QPS优先级策略
        /// Key: 通道路径（如 "/ws"）
        /// Value: 该通道的优先级策略配置
        /// </summary>
        public Dictionary<string, ChannelQpsPriorityPolicy> ChannelPolicies { get; set; } = new Dictionary<string, ChannelQpsPriorityPolicy>();
    }

    /// <summary>
    /// 通道级别的QPS优先级策略
    /// </summary>
    public class ChannelQpsPriorityPolicy
    {
        /// <summary>
        /// 该通道的总带宽限制（字节/秒）
        /// </summary>
        public long? ChannelBandwidthLimit { get; set; }

        /// <summary>
        /// 该通道的优先级配置（覆盖全局配置）
        /// </summary>
        public Dictionary<int, double> PriorityBandwidthRatios { get; set; } = new Dictionary<int, double>();

        /// <summary>
        /// 该通道的默认优先级
        /// </summary>
        public int? DefaultPriority { get; set; }

        /// <summary>
        /// 该通道的默认优先级带宽比例
        /// </summary>
        public double? DefaultPriorityBandwidthRatio { get; set; }
    }
}

