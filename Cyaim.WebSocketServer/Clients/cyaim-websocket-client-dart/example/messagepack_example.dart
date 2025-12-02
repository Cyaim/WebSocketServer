/// Example usage of cyaim_websocket_client with MessagePack protocol
/// 
/// Run with: dart run example/messagepack_example.dart

import 'package:cyaim_websocket_client/cyaim_websocket_client.dart';

Future<void> main() async {
  print('=== MessagePack Protocol Example ===\n');

  try {
    // Create options with MessagePack protocol
    final options = WebSocketClientOptions();
    options.protocol = SerializationProtocol.messagePack;
    
    // Create factory with MessagePack options
    final factory = WebSocketClientFactory(
      'http://localhost:5000',
      '/ws',
      options,
    );
    
    print('1. Creating client with MessagePack protocol...');
    final client = factory.createClient();
    
    print('2. Connecting to server...');
    await client.connect();
    print('   ✓ Connected');
    
    // Get endpoints
    print('\n3. Fetching endpoints...');
    final endpoints = await factory.getEndpoints();
    print('   Found ${endpoints.length} endpoints');
    
    // Send request using MessagePack
    if (endpoints.isNotEmpty) {
      print('\n4. Sending request with MessagePack...');
      final target = endpoints.first.target;
      print('   Target: $target');
      
      try {
        final result = await client.sendRequest<Map<String, dynamic>>(target);
        print('   ✓ Response received: $result');
      } catch (e) {
        print('   ✗ Error: $e');
      }
    }
    
    // Disconnect
    print('\n5. Disconnecting...');
    await client.disconnect();
    print('   ✓ Disconnected');
    
    print('\n=== Example completed ===');
    print('\nNote: Server must support MessagePack protocol');
  } catch (e) {
    print('Error: $e');
    print('\nNote: Make sure the server is running and supports MessagePack');
  }
}

