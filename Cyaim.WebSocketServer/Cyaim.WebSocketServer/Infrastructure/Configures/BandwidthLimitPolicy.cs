using System;
using System.Collections.Generic;

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    /// <summary>
    /// 带宽限速策略配置
    /// </summary>
    public class BandwidthLimitPolicy
    {
        /// <summary>
        /// 是否启用限速策略
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// 全局服务级别：每个通道的限速策略（字节/秒）
        /// Key: 通道路径（如 "/ws"）
        /// Value: 限速值（字节/秒）
        /// </summary>
        public Dictionary<string, long> GlobalChannelBandwidthLimit { get; set; } = new Dictionary<string, long>();

        /// <summary>
        /// 通道级别（单个连接）：最低带宽保障（字节/秒）
        /// Key: 通道路径
        /// Value: 最低带宽保障
        /// </summary>
        public Dictionary<string, long> ChannelMinBandwidthGuarantee { get; set; } = new Dictionary<string, long>();

        /// <summary>
        /// 通道级别（单个连接）：最高带宽限制（字节/秒）
        /// Key: 通道路径
        /// Value: 最高带宽限制
        /// </summary>
        public Dictionary<string, long> ChannelMaxBandwidthLimit { get; set; } = new Dictionary<string, long>();

        /// <summary>
        /// 通道级别（多个连接）：是否启用平均分配带宽策略
        /// Key: 通道路径
        /// Value: 是否启用
        /// </summary>
        public Dictionary<string, bool> ChannelEnableAverageBandwidth { get; set; } = new Dictionary<string, bool>();

        /// <summary>
        /// 通道级别（多个连接）：链接最低带宽保障（字节/秒）
        /// Key: 通道路径
        /// Value: 每个连接的最低带宽保障
        /// </summary>
        public Dictionary<string, long> ChannelConnectionMinBandwidthGuarantee { get; set; } = new Dictionary<string, long>();

        /// <summary>
        /// 通道级别（多个连接）：链接最高带宽限制（字节/秒）
        /// Key: 通道路径
        /// Value: 每个连接的最高带宽限制
        /// </summary>
        public Dictionary<string, long> ChannelConnectionMaxBandwidthLimit { get; set; } = new Dictionary<string, long>();

        /// <summary>
        /// WebSocket端点级别：每个端点的最高限速（字节/秒）
        /// Key: 端点路径（如 "controller.action"）
        /// Value: 最高限速
        /// </summary>
        public Dictionary<string, long> EndPointMaxBandwidthLimit { get; set; } = new Dictionary<string, long>();

        /// <summary>
        /// WebSocket端点级别：每个端点的最低带宽保障（字节/秒）
        /// Key: 端点路径
        /// Value: 最低带宽保障
        /// </summary>
        public Dictionary<string, long> EndPointMinBandwidthGuarantee { get; set; } = new Dictionary<string, long>();
    }
}

