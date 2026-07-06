import aiohttp
from typing import List, Optional
from .websocket_client import WebSocketClient
from .websocket_client_options import WebSocketClientOptions
from .websocket_endpoint_info import WebSocketEndpointInfo

class WebSocketClientFactory:
    """Factory for creating WebSocket client proxies based on server endpoints"""
    
    def __init__(
        self, 
        server_base_url: str, 
        channel: str = "/ws",
        options: Optional[WebSocketClientOptions] = None
    ):
        self.server_base_url = server_base_url.rstrip('/')
        self.channel = channel
        self.options = options or WebSocketClientOptions()
        self._cached_endpoints: Optional[List[WebSocketEndpointInfo]] = None

    async def get_endpoints(self) -> List[WebSocketEndpointInfo]:
        """Get endpoints from server"""
        if self._cached_endpoints:
            return self._cached_endpoints

        url = f"{self.server_base_url}/ws_server/api/endpoints"
        
        async with aiohttp.ClientSession() as session:
            async with session.get(url) as response:
                if response.status != 200:
                    raise RuntimeError(f"HTTP error! status: {response.status}")
                
                data = await response.json()
                
                if data.get('success') and data.get('data'):
                    # Server JSON uses camelCase (methodPath/fullName); map to the dataclass fields.
                    # 服务端 JSON 为 camelCase（methodPath/fullName），映射到数据类字段。
                    self._cached_endpoints = [
                        WebSocketEndpointInfo(
                            controller=ep.get('controller', ''),
                            action=ep.get('action', ''),
                            method_path=ep.get('methodPath') or ep.get('method_path', ''),
                            methods=ep.get('methods', []),
                            full_name=ep.get('fullName') or ep.get('full_name', ''),
                            target=ep.get('target', ''),
                        ) for ep in data['data']
                    ]
                    return self._cached_endpoints
                
                raise RuntimeError(data.get('error', 'Failed to fetch endpoints'))

    def create_client(self) -> WebSocketClient:
        """Create a client"""
        ws_uri = self.server_base_url.replace('http://', 'ws://').replace('https://', 'wss://')
        return WebSocketClient(ws_uri, self.channel, self.options)

