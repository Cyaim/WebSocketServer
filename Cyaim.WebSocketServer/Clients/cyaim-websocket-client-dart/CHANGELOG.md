# Changelog

## 1.0.0

### Added
- Initial release
- Support for JSON protocol (text messages)
- Support for MessagePack protocol (binary messages)
- Automatic endpoint discovery from server
- Type-safe request and response handling
- Error handling and timeout support
- Async/await support

### Features
- `WebSocketClient`: Core WebSocket client for connecting to server
- `WebSocketClientFactory`: Factory for creating clients and discovering endpoints
- `WebSocketClientOptions`: Configuration options including protocol selection
- `SerializationProtocol`: Enum for selecting JSON or MessagePack protocol
- `WebSocketEndpointInfo`: Model for endpoint information

