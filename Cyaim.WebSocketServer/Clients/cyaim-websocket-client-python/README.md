# cyaim-websocket-client-python

WebSocket client for Cyaim.WebSocketServer with automatic endpoint discovery (Python)

## Installation

```bash
pip install -e .
```

## Usage

```python
import asyncio
from cyaim_websocket_client import WebSocketClientFactory

async def main():
    factory = WebSocketClientFactory('http://localhost:5000', '/ws')
    client = factory.create_client()
    
    await client.connect()
    
    # Get forecasts
    forecasts = await client.send_request('weatherforecast.get')
    
    # Get forecast by city
    forecast = await client.send_request(
        'weatherforecast.getbycity',
        {'city': 'Beijing'}
    )
    
    await client.disconnect()

if __name__ == '__main__':
    asyncio.run(main())
```

## License

MIT

