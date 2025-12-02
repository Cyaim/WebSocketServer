/// Example usage of cyaim_websocket_client
/// 
/// Run with: dart run example/example.dart

import 'package:cyaim_websocket_client/cyaim_websocket_client.dart';

Future<void> main() async {
  print('=== Cyaim WebSocket Client Example ===\n');

  try {
    // Create factory
    final factory = WebSocketClientFactory('http://localhost:5000', '/ws');
    
    // Get available endpoints
    print('1. Fetching endpoints from server...');
    final endpoints = await factory.getEndpoints();
    print('   Found ${endpoints.length} endpoints');
    for (final endpoint in endpoints.take(3)) {
      print('   - ${endpoint.target} (${endpoint.controller}.${endpoint.action})');
    }
    
    // Create client
    print('\n2. Creating WebSocket client...');
    final client = factory.createClient();
    
    // Connect
    print('3. Connecting to server...');
    await client.connect();
    print('   ✓ Connected');
    
    // Send request (if endpoints available)
    if (endpoints.isNotEmpty) {
      print('\n4. Sending request...');
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
  } catch (e) {
    print('Error: $e');
    print('\nNote: Make sure the server is running on http://localhost:5000');
  }
}

