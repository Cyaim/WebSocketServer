import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';
import 'package:web_socket_channel/web_socket_channel.dart';
import 'package:msgpack_dart/msgpack_dart.dart' show serialize, deserialize;
import 'websocket_client_options.dart';

/// WebSocket client for connecting to Cyaim.WebSocketServer
/// 用于连接到 Cyaim.WebSocketServer 的 WebSocket 客户端
class WebSocketClient {
  final String serverUri;
  final String channel;
  final WebSocketClientOptions options;
  WebSocketChannel? _channel;
  final Map<String, CompletableFuture<MvcResponseScheme>> _pendingResponses = {};

  WebSocketClient(this.serverUri, this.channel, [WebSocketClientOptions? options])
      : options = options ?? WebSocketClientOptions();

  /// Connect to server / 连接到服务器
  Future<void> connect() async {
    final uri = '${serverUri.replaceAll(RegExp(r'/$'), '')}$channel';
    _channel = WebSocketChannel.connect(Uri.parse(uri));
    
    _channel!.stream.listen(
      (message) {
        if (message is String) {
          handleTextMessage(message);
        } else if (message is Uint8List) {
          handleBinaryMessage(message);
        } else if (message is List<int>) {
          handleBinaryMessage(Uint8List.fromList(message));
        }
      },
      onError: (error) {
        print('WebSocket error: $error');
      },
      onDone: () {
        print('WebSocket connection closed');
      },
    );
  }

  /// Send request and wait for response / 发送请求并等待响应
  Future<T> sendRequest<T>(String target, [dynamic requestBody]) async {
    if (_channel == null) {
      throw StateError('WebSocket is not connected. Call connect() first.');
    }

    final requestId = '${DateTime.now().millisecondsSinceEpoch}_${Uri.encodeComponent(target)}';
    final request = <String, dynamic>{
      'id': requestId,
      'target': target,
      if (requestBody != null) 'body': requestBody,
    };

    final completer = CompletableFuture<MvcResponseScheme>();
    _pendingResponses[requestId] = completer;

    // 根据协议选择序列化方式
    if (options.protocol == SerializationProtocol.messagePack) {
      final requestBytes = serialize(request);
      _channel!.sink.add(requestBytes);
    } else {
      _channel!.sink.add(jsonEncode(request));
    }

    // Timeout after 30 seconds
    Timer(const Duration(seconds: 30), () {
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
      // 对于可空类型，返回 null
      // 注意：这需要调用者使用可空类型，如 Future<Map<String, dynamic>?>
      return null as T;
    }

    // 如果 body 已经是目标类型，直接返回
    if (response.body is T) {
      return response.body as T;
    }

    // 否则通过 JSON 转换
    return jsonDecode(jsonEncode(response.body)) as T;
  }

  void handleTextMessage(String message) {
    if (options.protocol != SerializationProtocol.json) {
      return;
    }
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

  void handleBinaryMessage(Uint8List bytes) {
    if (options.protocol != SerializationProtocol.messagePack) {
      return;
    }
    try {
      final decoded = deserialize(bytes);
      // 确保 decoded 是 Map 类型
      final Map<String, dynamic> responseMap;
      if (decoded is Map) {
        responseMap = Map<String, dynamic>.from(decoded);
      } else {
        throw FormatException('Invalid MessagePack response format');
      }
      final response = MvcResponseScheme.fromJson(responseMap);
      final completer = _pendingResponses.remove(response.id);
      if (completer != null) {
        completer.complete(response);
      }
    } catch (e) {
      print('Failed to parse MessagePack response: $e');
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

