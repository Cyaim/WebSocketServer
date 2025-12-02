import 'dart:convert';
import 'package:http/http.dart' as http;
import 'websocket_client.dart';
import 'websocket_client_options.dart';
import 'websocket_endpoint_info.dart';

/// Factory for creating WebSocket client proxies based on server endpoints
/// 基于服务器端点创建 WebSocket 客户端代理的工厂
class WebSocketClientFactory {
  final String serverBaseUrl;
  final String channel;
  final WebSocketClientOptions options;
  List<WebSocketEndpointInfo>? _cachedEndpoints;

  WebSocketClientFactory(this.serverBaseUrl, this.channel, [WebSocketClientOptions? options])
      : options = options ?? WebSocketClientOptions();

  /// Get endpoints from server / 从服务器获取端点
  Future<List<WebSocketEndpointInfo>> getEndpoints() async {
    if (_cachedEndpoints != null) {
      return _cachedEndpoints!;
    }

    try {
      final url = '${serverBaseUrl.replaceAll(RegExp(r'/$'), '')}/ws_server/api/endpoints';
      final response = await http.get(Uri.parse(url));

      if (response.statusCode != 200) {
        throw Exception('HTTP error! status: ${response.statusCode}');
      }

      final apiResponse = jsonDecode(response.body) as Map<String, dynamic>;
      
      if (apiResponse['success'] == true && apiResponse['data'] != null) {
        final data = apiResponse['data'] as List<dynamic>;
        _cachedEndpoints = data
            .map((e) => WebSocketEndpointInfo.fromJson(e as Map<String, dynamic>))
            .toList();
        return _cachedEndpoints!;
      }

      throw Exception(apiResponse['error'] ?? 'Failed to fetch endpoints');
    } catch (e) {
      throw Exception('Error fetching WebSocket endpoints from server: $e');
    }
  }

  /// Create a client / 创建客户端
  WebSocketClient createClient() {
    final wsUri = serverBaseUrl
        .replaceAll('http://', 'ws://')
        .replaceAll('https://', 'wss://');
    return WebSocketClient(wsUri, channel, options);
  }
}

