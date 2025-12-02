/// 简单的验证脚本，用于检查 Dart 客户端库的基本功能
/// 
/// 使用方法：
/// 1. 确保已安装依赖：dart pub get
/// 2. 运行此脚本：dart test_verify.dart

import 'package:cyaim_websocket_client/cyaim_websocket_client.dart';

void main() {
  print('=== Dart 客户端库验证 ===\n');
  
  // 1. 检查 SerializationProtocol 枚举
  print('1. 检查 SerializationProtocol 枚举...');
  final jsonProtocol = SerializationProtocol.json;
  final msgpackProtocol = SerializationProtocol.messagePack;
  print('   ✓ JSON: ${jsonProtocol.value}');
  print('   ✓ MessagePack: ${msgpackProtocol.value}');
  
  // 2. 检查 WebSocketClientOptions
  print('\n2. 检查 WebSocketClientOptions...');
  final options = WebSocketClientOptions();
  print('   ✓ 默认协议: ${options.protocol}');
  print('   ✓ validateAllMethods: ${options.validateAllMethods}');
  print('   ✓ lazyLoadEndpoints: ${options.lazyLoadEndpoints}');
  
  // 3. 检查 MessagePack 选项
  print('\n3. 检查 MessagePack 选项...');
  final msgpackOptions = WebSocketClientOptions();
  msgpackOptions.protocol = SerializationProtocol.messagePack;
  print('   ✓ MessagePack 协议设置成功: ${msgpackOptions.protocol}');
  
  // 4. 检查 WebSocketClientFactory
  print('\n4. 检查 WebSocketClientFactory...');
  final factory = WebSocketClientFactory('http://localhost:5000', '/ws', options);
  print('   ✓ Factory 创建成功');
  
  // 5. 检查 WebSocketClient
  print('\n5. 检查 WebSocketClient...');
  final client = factory.createClient();
  print('   ✓ Client 创建成功');
  
  // 6. 检查 WebSocketEndpointInfo
  print('\n6. 检查 WebSocketEndpointInfo...');
  final endpointInfo = WebSocketEndpointInfo(
    controller: 'Test',
    action: 'Get',
    methodPath: 'test.get',
    methods: ['GET'],
    fullName: 'Test.Get',
    target: 'test.get',
  );
  print('   ✓ EndpointInfo 创建成功');
  print('   ✓ Target: ${endpointInfo.target}');
  
  print('\n=== 所有检查通过！===');
  print('\n注意：此脚本仅验证代码结构，不进行实际网络连接。');
  print('要测试实际功能，请参考 README.md 中的示例代码。');
}

