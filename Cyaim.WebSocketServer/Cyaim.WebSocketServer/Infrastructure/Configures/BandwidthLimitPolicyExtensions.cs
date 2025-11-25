using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    /// <summary>
    /// 带宽限速策略配置扩展方法
    /// </summary>
    public static class BandwidthLimitPolicyExtensions
    {
        /// <summary>
        /// 从IConfiguration加载限速策略（使用配置绑定）
        /// </summary>
        /// <param name="policy">限速策略对象</param>
        /// <param name="configuration">配置对象</param>
        /// <param name="sectionName">配置节点名称，默认为 "BandwidthLimitPolicy"</param>
        public static void LoadFromConfiguration(this BandwidthLimitPolicy policy, IConfiguration configuration, string sectionName = "BandwidthLimitPolicy")
        {
            if (policy == null || configuration == null)
                return;

            var section = configuration.GetSection(sectionName);
            if (!section.Exists())
                return;

            // 使用配置绑定，自动将配置绑定到对象属性
            // 这会自动处理字典类型的绑定
            section.Bind(policy);
        }

        /// <summary>
        /// 从JSON文件加载限速策略
        /// </summary>
        /// <param name="policy">限速策略对象</param>
        /// <param name="jsonFilePath">JSON文件路径</param>
        /// <param name="sectionName">配置节点名称，默认为 "BandwidthLimitPolicy"</param>
        public static void LoadFromJsonFile(this BandwidthLimitPolicy policy, string jsonFilePath, string sectionName = "BandwidthLimitPolicy")
        {
            if (policy == null || string.IsNullOrEmpty(jsonFilePath) || !File.Exists(jsonFilePath))
                return;

            try
            {
                var jsonContent = File.ReadAllText(jsonFilePath);
                var jsonDocument = JsonDocument.Parse(jsonContent);

                if (jsonDocument.RootElement.TryGetProperty(sectionName, out var sectionElement))
                {
                    var jsonString = sectionElement.GetRawText();
                    var configPolicy = JsonSerializer.Deserialize<BandwidthLimitPolicy>(jsonString, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (configPolicy != null)
                    {
                        policy.Enabled = configPolicy.Enabled;
                        policy.GlobalChannelBandwidthLimit = configPolicy.GlobalChannelBandwidthLimit ?? new Dictionary<string, long>();
                        policy.ChannelMinBandwidthGuarantee = configPolicy.ChannelMinBandwidthGuarantee ?? new Dictionary<string, long>();
                        policy.ChannelMaxBandwidthLimit = configPolicy.ChannelMaxBandwidthLimit ?? new Dictionary<string, long>();
                        policy.ChannelEnableAverageBandwidth = configPolicy.ChannelEnableAverageBandwidth ?? new Dictionary<string, bool>();
                        policy.ChannelConnectionMinBandwidthGuarantee = configPolicy.ChannelConnectionMinBandwidthGuarantee ?? new Dictionary<string, long>();
                        policy.ChannelConnectionMaxBandwidthLimit = configPolicy.ChannelConnectionMaxBandwidthLimit ?? new Dictionary<string, long>();
                        policy.EndPointMaxBandwidthLimit = configPolicy.EndPointMaxBandwidthLimit ?? new Dictionary<string, long>();
                        policy.EndPointMinBandwidthGuarantee = configPolicy.EndPointMinBandwidthGuarantee ?? new Dictionary<string, long>();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"加载限速策略配置文件失败: {jsonFilePath}", ex);
            }
        }

    }
}

