using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// I18nText
    /// 
    /// Json Key:Value格式，Key对应本类的字段
    /// </summary>
    public static class I18nText
    {
        static I18nText()
        {
            try
            {
                var cultureInfo = CultureInfo.CurrentCulture;
                UpdateI18nText(cultureInfo);
            }
            catch { }

        }

        /// <summary>
        /// Update i18n text
        /// </summary>
        /// <param name="cultureInfo"></param>
        public static void UpdateI18nText(CultureInfo cultureInfo)
        {
            try
            {
                // 找本地化资源文件
                string resPath;
                if (Path.IsPathRooted(I18nResourcePath))
                {
                    resPath = Path.Combine(I18nResourcePath, cultureInfo.Name + I18nResourceFileSuffix);
                }
                else
                {
                    resPath = Path.Combine(AppContext.BaseDirectory, I18nResourcePath, cultureInfo.Name + I18nResourceFileSuffix);
                }

                UpdateI18nText(resPath);
            }
            catch { throw; }
        }

        /// <summary>
        /// Update i18n text
        /// </summary>
        /// <param name="i18nResourcePath"></param>
        public static void UpdateI18nText(string i18nResourcePath)
        {
            try
            {
                if (File.Exists(i18nResourcePath))
                {
                    var textKV = JsonSerializer.Deserialize<Dictionary<string, string>>(i18nResourcePath);

                    // 公开、静态的没有标记JsonIgnore的字段作为i18n的文本字段
                    var fields = typeof(I18nText).GetFields(BindingFlags.Public | BindingFlags.Static).Where(x => !x.GetCustomAttributes<JsonIgnoreAttribute>().Any());
                    foreach (var field in fields)
                    {
                        if (field != null && field.IsLiteral)
                        {
                            continue;
                        }
                        textKV.TryGetValue(field.Name, out string val);
                        // 设置字段的值
                        field.SetValue(null, val);
                    }
                }

            }
            catch
            {
                throw;
            }
        }

        /// <summary>
        /// 国际化文件夹路径
        /// </summary>
        [JsonIgnore]
        public static string I18nResourcePath { get; set; } = "i18n";
        /// <summary>
        /// 语言文件后缀
        /// </summary>
        [JsonIgnore]
        public static string I18nResourceFileSuffix { get; set; } = ".json";

        /// <summary>
        /// WebSocket 交互文本模板
        /// {RemoteIpAddress}:{RemotePort} ({ConnectionId}) -> {Msg}
        /// </summary>
        [JsonIgnore]
        public const string WS_INTERACTIVE_TEXT_TEMPALTE = @"{0}:{1} ({2})-> {3}";

        public static readonly string WebSocketCloseStatus_Empty = "No error specified.";
        public static readonly string WebSocketCloseStatus_EndpointUnavailable = "Indicates an endpoint is being removed. Either the server or client will become unavailable.";
        public static readonly string WebSocketCloseStatus_InternalServerError = "The connection will be closed by the server because of an error on the server.";
        public static readonly string WebSocketCloseStatus_InvalidMessageType = "The client or server is terminating the connection because it cannot accept the data type it received.";
        public static readonly string WebSocketCloseStatus_InvalidPayloadData = "The client or server is terminating the connection because it has received data inconsistent with the message type.";
        public static readonly string WebSocketCloseStatus_MandatoryExtension = "The client is terminating the connection because it expected the server to negotiate an extension.";
        public static readonly string WebSocketCloseStatus_MessageTooBig = "Reserved for future use.";
        public static readonly string WebSocketCloseStatus_NormalClosure = "The connection has closed after the request was fulfilled.";
        public static readonly string WebSocketCloseStatus_PolicyViolation = "The connection will be closed because an endpoint has received a messagethat violates its policy.";
        public static readonly string WebSocketCloseStatus_ProtocolError = "The client or server is terminating the connection because of a protocol error.";
        public static readonly string WebSocketCloseStatus_ConnectionShutdown = "The client or server is terminating the connection because of a protocol error.";


        public static readonly string ConnectionEntry_Connected = "Connected.";
        public static readonly string ConnectionEntry_ConnectionAlreadyExists = "Connection already exists.";
        public static readonly string ConnectionEntry_DisconnectedInternalExceptions = "Connection disconnected due to one or more internal exceptions." + Environment.NewLine;
        public static readonly string ConnectionEntry_ConnectionDenied = "Connection denied:request header error.";
        public static readonly string ConnectionEntry_RequestSizeMaximumLimit = "Request size exceeds maximum limit.";
        public static readonly string ConnectionEntry_ReceivingClientDataException = "An exception occurred while receiving client data.";
        public static readonly string ConnectionEntry_AbortedReceivingData = "Aborted receiving data." + Environment.NewLine;
        public static readonly string MvcForwardSendData_RequestParsingError = "Request parsing error." + Environment.NewLine;
        public static readonly string MvcForwardSendData_RequestBodyFormatError = "Request body format error." + Environment.NewLine;
        public static readonly string MvcForwardSendData_RequestBodyParameterFormatError = "Parameter data formatting exception in the request body." + Environment.NewLine;
        public static readonly string MvcDistributeAsync_EmptyDI = "Cannot inject target constructor parameter because DI container WebSocketRouteOption.ApplicationServiceCollection is null.";
        public static readonly string MvcDistributeAsync_Target = "Target:";
        public static readonly string OnDisconnected_Disconnected = "Disconnected." + Environment.NewLine;


        public static readonly string NowWebSocketOn = "Now websocket on: ";
        public static readonly string DefineWebSocketChannelOn = "Define websocket channel on: ";


        public static readonly string AtThisStagePipelineNotSupport = "This pipeline type is not supported at this stage and cannot be null. It should be: ";

        public static readonly string MvcDistributeAsync_EndPointNotFound = "Endpoint not found: ";


    }
}
