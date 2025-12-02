# cyaim-websocket-client-dart

WebSocket client for Cyaim.WebSocketServer with automatic endpoint discovery (Dart/Flutter)

## Features

- ✅ Automatic endpoint discovery from server
- ✅ Type-safe request and response
- ✅ Support for both JSON and MessagePack protocols
- ✅ Async/await support
- ✅ Error handling and timeout support
- ✅ Flutter/Dart compatible

## Installation

Add to your `pubspec.yaml`:

```yaml
dependencies:
  cyaim_websocket_client:
    path: ../cyaim-websocket-client-dart
```

Or if published to pub.dev:

```yaml
dependencies:
  cyaim_websocket_client: ^1.0.0
```

Then run:

```bash
dart pub get
```

## Usage

### Basic Usage (JSON Protocol)

```dart
import 'package:cyaim_websocket_client/cyaim_websocket_client.dart';

void main() async {
  final factory = WebSocketClientFactory('http://localhost:5000', '/ws');
  final client = factory.createClient();
  
  await client.connect();
  
  // Get forecasts
  final forecasts = await client.sendRequest<List<Map<String, dynamic>>>(
    'weatherforecast.get',
  );
  
  print('Forecasts: $forecasts');
  
  // Get forecast by city
  final forecast = await client.sendRequest<Map<String, dynamic>>(
    'weatherforecast.getbycity',
    {'city': 'Beijing'},
  );
  
  print('Forecast: $forecast');
  
  await client.disconnect();
}
```

### Using MessagePack Protocol

```dart
import 'package:cyaim_websocket_client/cyaim_websocket_client.dart';

void main() async {
  final options = WebSocketClientOptions();
  options.protocol = SerializationProtocol.messagePack;
  
  final factory = WebSocketClientFactory(
    'http://localhost:5000', 
    '/ws', 
    options
  );
  final client = factory.createClient();
  
  await client.connect();
  
  final forecasts = await client.sendRequest<List<Map<String, dynamic>>>(
    'weatherforecast.get',
  );
  
  print('Forecasts: $forecasts');
  
  await client.disconnect();
}
```

### Using Factory to Get Endpoints

```dart
import 'package:cyaim_websocket_client/cyaim_websocket_client.dart';

void main() async {
  final factory = WebSocketClientFactory('http://localhost:5000', '/ws');
  
  // Get all available endpoints
  final endpoints = await factory.getEndpoints();
  
  for (final endpoint in endpoints) {
    print('Endpoint: ${endpoint.target}');
    print('  Controller: ${endpoint.controller}');
    print('  Action: ${endpoint.action}');
    print('  Method Path: ${endpoint.methodPath}');
  }
  
  final client = factory.createClient();
  await client.connect();
  
  // Use endpoint target
  final result = await client.sendRequest<Map<String, dynamic>>(
    endpoints.first.target,
  );
  
  await client.disconnect();
}
```

## API Reference

### WebSocketClientFactory

Factory for creating WebSocket clients.

#### Constructor

```dart
WebSocketClientFactory(
  String serverBaseUrl,
  String channel, [
  WebSocketClientOptions? options
])
```

- `serverBaseUrl`: Server base URL (e.g., "http://localhost:5000")
- `channel`: WebSocket channel (default: "/ws")
- `options`: Optional client options

#### Methods

- `Future<List<WebSocketEndpointInfo>> getEndpoints()`: Get all available endpoints from server
- `WebSocketClient createClient()`: Create a WebSocket client instance

### WebSocketClient

WebSocket client for connecting to server.

#### Methods

- `Future<void> connect()`: Connect to server
- `Future<T> sendRequest<T>(String target, [dynamic requestBody])`: Send request and wait for response
- `Future<void> disconnect()`: Disconnect from server

### WebSocketClientOptions

Client configuration options.

#### Properties

- `SerializationProtocol protocol`: Serialization protocol (default: `SerializationProtocol.json`)
  - `SerializationProtocol.json`: JSON protocol (text messages)
  - `SerializationProtocol.messagePack`: MessagePack protocol (binary messages)
- `bool validateAllMethods`: Whether to validate all methods (default: false)
- `bool lazyLoadEndpoints`: Whether to lazy load endpoints (default: false)
- `bool throwOnEndpointNotFound`: Whether to throw exception if endpoint not found (default: true)

### SerializationProtocol

Enum for serialization protocols.

- `json`: JSON protocol (text messages)
- `messagePack`: MessagePack protocol (binary messages)

## Server Requirements

The server must provide the following API endpoint:

### GET /ws_server/api/endpoints

Returns all available WebSocket endpoints.

Response format:

```json
{
  "success": true,
  "data": [
    {
      "controller": "WeatherForecast",
      "action": "Get",
      "methodPath": "weatherforecast.get",
      "methods": ["GET"],
      "fullName": "WeatherForecast.Get",
      "target": "weatherforecast.get"
    }
  ],
  "error": null
}
```

## Error Handling

All methods throw exceptions on error:

- `StateError`: WebSocket is not connected
- `Exception`: Request failed, timeout, or server error
- `FormatException`: Invalid response format

Example:

```dart
try {
  final result = await client.sendRequest<Map<String, dynamic>>('endpoint');
} catch (e) {
  print('Error: $e');
}
```

## Timeout

Requests timeout after 30 seconds by default. If a timeout occurs, an `Exception` with message "Request timeout" is thrown.

## Notes

1. **Connection Management**: You must call `connect()` before sending requests
2. **Protocol Selection**: Choose JSON for easier debugging, MessagePack for better performance
3. **Type Safety**: Use generic type parameter `T` in `sendRequest<T>()` for type-safe responses
4. **Error Handling**: Always wrap requests in try-catch blocks
5. **Server Compatibility**: Ensure server supports the selected protocol (JSON or MessagePack)

## License

MIT License

Copyright © Cyaim Studio
