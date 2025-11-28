# @cyaim/websocket-client

WebSocket client for Cyaim.WebSocketServer with automatic endpoint discovery (JavaScript/TypeScript)

## Installation

```bash
npm install @cyaim/websocket-client
```

## Usage

### Basic Example

```typescript
import { WebSocketClientFactory, endpoint } from '@cyaim/websocket-client';

// Define interface / 定义接口
interface IWeatherService {
  getForecasts(): Promise<WeatherForecast[]>;
  getForecast(city: string): Promise<WeatherForecast>;
}

// Create factory / 创建工厂
const factory = new WebSocketClientFactory('http://localhost:5000', '/ws');

// Create client / 创建客户端
const weatherService = await factory.createClient<IWeatherService>({
  getForecasts: async () => {},
  getForecast: async (city: string) => {}
});

// Use client / 使用客户端
const forecasts = await weatherService.getForecasts();
const forecast = await weatherService.getForecast('Beijing');
```

### Using Endpoint Decorator

```typescript
import { endpoint } from '@cyaim/websocket-client';

interface IUserService {
  getUserById(id: number): Promise<User>;
}

const userService = await factory.createClient<IUserService>({
  [endpoint('user.getbyid')]: async (id: number) => {}
});
```

### With Options

```typescript
import { WebSocketClientOptions } from '@cyaim/websocket-client';

const options = new WebSocketClientOptions();
options.lazyLoadEndpoints = true;  // Load endpoints on demand
options.validateAllMethods = false;

const factory = new WebSocketClientFactory('http://localhost:5000', '/ws', options);
```

## API

### WebSocketClientFactory

#### Constructor

```typescript
new WebSocketClientFactory(serverBaseUrl: string, channel?: string, options?: WebSocketClientOptions)
```

#### Methods

- `getEndpoints(): Promise<WebSocketEndpointInfo[]>` - Get all endpoints from server
- `createClient<T>(interfaceDefinition: T): Promise<T>` - Create client proxy

### WebSocketClientOptions

- `validateAllMethods: boolean` - Validate all methods have endpoints (default: false)
- `lazyLoadEndpoints: boolean` - Load endpoints on demand (default: false)
- `throwOnEndpointNotFound: boolean` - Throw error if endpoint not found (default: true)

## License

MIT

