import asyncio
import json
import uuid
from typing import Dict, Any, Optional, TypeVar, Type
import websockets
from websockets.client import WebSocketClientProtocol
import msgpack
from .websocket_client_options import WebSocketClientOptions, SerializationProtocol

T = TypeVar('T')

class WebSocketClient:
    """WebSocket client for connecting to Cyaim.WebSocketServer"""
    
    def __init__(self, server_uri: str, channel: str = "/ws", options: Optional[WebSocketClientOptions] = None):
        self.server_uri = server_uri.rstrip('/')
        self.channel = channel
        self.options = options or WebSocketClientOptions()
        self.websocket: Optional[WebSocketClientProtocol] = None
        self.pending_responses: Dict[str, asyncio.Future] = {}

    async def connect(self):
        """Connect to server"""
        uri = f"{self.server_uri}{self.channel}"
        self.websocket = await websockets.connect(uri)
        
        # Start listening for messages
        asyncio.create_task(self._listen())

    async def _listen(self):
        """Listen for incoming messages"""
        if not self.websocket:
            return
            
        try:
            async for message in self.websocket:
                await self._handle_message(message)
        except websockets.exceptions.ConnectionClosed:
            pass

    async def _handle_message(self, message):
        """Handle incoming message"""
        try:
            if isinstance(message, str):
                # Text message (JSON)
                if self.options.protocol != SerializationProtocol.JSON:
                    return
                response = json.loads(message)
            elif isinstance(message, bytes):
                # Binary message (MessagePack)
                if self.options.protocol != SerializationProtocol.MESSAGEPACK:
                    return
                response = msgpack.unpackb(message, raw=False)
            else:
                return
                
            request_id = response.get('id')
            
            if request_id and request_id in self.pending_responses:
                future = self.pending_responses.pop(request_id)
                if not future.done():
                    future.set_result(response)
        except Exception as e:
            print(f"Failed to parse response: {e}")

    async def send_request(
        self, 
        target: str, 
        request_body: Optional[Any] = None
    ) -> Any:
        """Send request and wait for response"""
        if not self.websocket or self.websocket.closed:
            raise RuntimeError("WebSocket is not connected. Call connect() first.")

        request_id = str(uuid.uuid4())
        request = {
            "id": request_id,
            "target": target,
        }
        
        if request_body is not None:
            request["body"] = request_body

        future = asyncio.Future()
        self.pending_responses[request_id] = future

        # 根据协议选择序列化方式
        if self.options.protocol == SerializationProtocol.MESSAGEPACK:
            request_bytes = msgpack.packb(request)
            await self.websocket.send(request_bytes)
        else:
            await self.websocket.send(json.dumps(request))

        # Timeout after 30 seconds
        try:
            response = await asyncio.wait_for(future, timeout=30.0)
            
            if response.get('status', 0) != 0:
                raise RuntimeError(response.get('msg', 'Unknown error'))
            
            return response.get('body')
        except asyncio.TimeoutError:
            self.pending_responses.pop(request_id, None)
            raise RuntimeError("Request timeout")
        finally:
            self.pending_responses.pop(request_id, None)

    async def disconnect(self):
        """Disconnect from server"""
        if self.websocket:
            await self.websocket.close()
            self.websocket = None
        self.pending_responses.clear()

