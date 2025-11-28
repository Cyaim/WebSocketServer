import 'dart:async';
import 'dart:convert';
import 'package:web_socket_channel/web_socket_channel.dart';

/// WebSocket client for connecting to Cyaim.WebSocketServer
/// 用于连接到 Cyaim.WebSocketServer 的 WebSocket 客户端
class WebSocketClient {
  final String serverUri;
  final String channel;
  WebSocketChannel? _channel;
  final Map<String, CompletableFuture<MvcResponseScheme>> _pendingResponses = {};

  WebSocketClient(this.serverUri, this.channel);

  /// Connect to server / 连接到服务器
  Future<void> connect() async {
    final uri = '${serverUri.replaceAll(RegExp(r'/$'), '')}$channel';
    _channel = WebSocketChannel.connect(Uri.parse(uri));
    
    _channel!.stream.listen((message) {
      handleMessage(message.toString());
    });
  }

  /// Send request and wait for response / 发送请求并等待响应
  Future<T> sendRequest<T>(String target, [dynamic requestBody]) async {
    if (_channel == null) {
      throw StateError('WebSocket is not connected. Call connect() first.');
    }

    final requestId = DateTime.now().millisecondsSinceEpoch.toString() + 
                     '_${Uri.encodeComponent(target)}';
    final request = {
      'id': requestId,
      'target': target,
      if (requestBody != null) 'body': requestBody,
    };

    final completer = CompletableFuture<MvcResponseScheme>();
    _pendingResponses[requestId] = completer;

    _channel!.sink.add(jsonEncode(request));

    // Timeout after 30 seconds
    Timer(Duration(seconds: 30), () {
      if (_pendingResponses.containsKey(requestId)) {
        _pendingResponses.remove(requestId);
        completer.completeError(Exception('Request timeout'));
      }
    });

    final response = await completer.future;
    
    if (response.status != 0) {
      throw Exception(response.msg ?? 'Unknown error');
    }

    if (response.body == null) {
      return null as T;
    }

    return jsonDecode(jsonEncode(response.body)) as T;
  }

  void handleMessage(String message) {
    try {
      final response = MvcResponseScheme.fromJson(jsonDecode(message));
      final completer = _pendingResponses.remove(response.id);
      if (completer != null) {
        completer.complete(response);
      }
    } catch (e) {
      print('Failed to parse response: $e');
    }
  }

  /// Disconnect from server / 断开服务器连接
  Future<void> disconnect() async {
    await _channel?.sink.close();
    _channel = null;
    _pendingResponses.clear();
  }
}

class MvcResponseScheme {
  final String id;
  final String target;
  final int status;
  final String? msg;
  final dynamic body;

  MvcResponseScheme({
    required this.id,
    required this.target,
    required this.status,
    this.msg,
    this.body,
  });

  factory MvcResponseScheme.fromJson(Map<String, dynamic> json) {
    return MvcResponseScheme(
      id: json['id'] as String,
      target: json['target'] as String,
      status: json['status'] as int,
      msg: json['msg'] as String?,
      body: json['body'],
    );
  }
}

class CompletableFuture<T> {
  final _completer = Completer<T>();

  Future<T> get future => _completer.future;

  void complete(T value) => _completer.complete(value);
  void completeError(Object error, [StackTrace? stackTrace]) => 
      _completer.completeError(error, stackTrace);
}

