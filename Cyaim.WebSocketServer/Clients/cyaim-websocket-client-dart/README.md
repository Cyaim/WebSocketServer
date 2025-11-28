# cyaim-websocket-client-dart

WebSocket client for Cyaim.WebSocketServer with automatic endpoint discovery (Dart)

## Installation

Add to your `pubspec.yaml`:

```yaml
dependencies:
  cyaim_websocket_client:
    path: ../cyaim-websocket-client-dart
```

## Usage

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
  
  // Get forecast by city
  final forecast = await client.sendRequest<Map<String, dynamic>>(
    'weatherforecast.getbycity',
    {'city': 'Beijing'},
  );
  
  await client.disconnect();
}
```

## License

MIT

