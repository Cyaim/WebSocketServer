/// WebSocket endpoint information / WebSocket 端点信息
class WebSocketEndpointInfo {
  final String controller;
  final String action;
  final String methodPath;
  final List<String> methods;
  final String fullName;
  final String target;

  WebSocketEndpointInfo({
    required this.controller,
    required this.action,
    required this.methodPath,
    required this.methods,
    required this.fullName,
    required this.target,
  });

  factory WebSocketEndpointInfo.fromJson(Map<String, dynamic> json) {
    return WebSocketEndpointInfo(
      controller: json['controller'] as String,
      action: json['action'] as String,
      methodPath: json['methodPath'] as String,
      methods: (json['methods'] as List?)?.map((e) => e.toString()).toList() ?? [],
      fullName: json['fullName'] as String,
      target: json['target'] as String,
    );
  }
}

