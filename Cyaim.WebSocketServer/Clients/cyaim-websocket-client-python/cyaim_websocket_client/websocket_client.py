import asyncio
import json
import uuid
from typing import Dict, Any, Optional, TypeVar
import websockets
import msgpack
from .websocket_client_options import WebSocketClientOptions, SerializationProtocol

T = TypeVar('T')


def _normalize_response(resp: Any) -> Dict[str, Any]:
    """Normalize a server response into {id, target, status, msg, body}.

    - JSON responses use PascalCase (Status/Id/Msg/Body).
    - MessagePack responses use integer [Key] and decode to a list
      [Status, Msg, RequestTime, CompleteTime, Id, Target, Body].
    将服务端响应归一化：JSON 为 PascalCase；MessagePack 为整数 [Key]（解码为列表）。
    """
    if isinstance(resp, (list, tuple)):
        return {
            "status": resp[0] if len(resp) > 0 else 0,
            "msg": resp[1] if len(resp) > 1 else None,
            "id": resp[4] if len(resp) > 4 else None,
            "target": resp[5] if len(resp) > 5 else None,
            "body": resp[6] if len(resp) > 6 else None,
        }
    if isinstance(resp, dict):
        def pick(*keys):
            for k in keys:
                if k in resp:
                    return resp[k]
            return None
        return {
            "status": pick("Status", "status", 0) or 0,
            "msg": pick("Msg", "msg"),
            "id": pick("Id", "id", 4),
            "target": pick("Target", "target", 5),
            "body": pick("Body", "body", 6),
        }
    return {"status": 0, "msg": None, "id": None, "target": None, "body": None}


class WebSocketClient:
    """WebSocket client for connecting to Cyaim.WebSocketServer"""

    def __init__(self, server_uri: str, channel: str = "/ws", options: Optional[WebSocketClientOptions] = None):
        self.server_uri = server_uri.rstrip('/')
        self.channel = channel
        self.options = options or WebSocketClientOptions()
        self.websocket = None
        self.pending_responses: Dict[str, asyncio.Future] = {}
        self._listen_task: Optional[asyncio.Task] = None

    async def connect(self):
        """Connect to server"""
        uri = f"{self.server_uri}{self.channel}"
        self.websocket = await websockets.connect(uri)
        self._listen_task = asyncio.create_task(self._listen())

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
                if self.options.protocol != SerializationProtocol.JSON:
                    return
                raw = json.loads(message)
            elif isinstance(message, (bytes, bytearray)):
                if self.options.protocol != SerializationProtocol.MESSAGEPACK:
                    return
                raw = msgpack.unpackb(bytes(message), raw=False)
            else:
                return

            response = _normalize_response(raw)
            request_id = response.get('id')
            if request_id is not None and request_id in self.pending_responses:
                future = self.pending_responses.pop(request_id)
                if not future.done():
                    future.set_result(response)
        except Exception as e:
            print(f"Failed to parse response: {e}")

    async def send_request(self, target: str, request_body: Optional[Any] = None) -> Any:
        """Send request and wait for response"""
        if not self.websocket:
            raise RuntimeError("WebSocket is not connected. Call connect() first.")

        request_id = str(uuid.uuid4())

        future: asyncio.Future = asyncio.get_event_loop().create_future()
        self.pending_responses[request_id] = future

        if self.options.protocol == SerializationProtocol.MESSAGEPACK:
            # Server MessagePackRequestScheme uses integer [Key(0)]Id/[Key(1)]Target/[Key(2)]Body
            # (array format), so encode as [id, target, body].
            request_bytes = msgpack.packb([request_id, target, request_body])
            await self.websocket.send(request_bytes)
        else:
            request = {"id": request_id, "target": target}
            if request_body is not None:
                request["body"] = request_body
            await self.websocket.send(json.dumps(request))

        try:
            response = await asyncio.wait_for(future, timeout=30.0)
            if response.get('status', 0) != 0:
                raise RuntimeError(response.get('msg') or 'Unknown error')
            return response.get('body')
        except asyncio.TimeoutError:
            self.pending_responses.pop(request_id, None)
            raise RuntimeError("Request timeout")
        finally:
            self.pending_responses.pop(request_id, None)

    async def disconnect(self):
        """Disconnect from server"""
        if self._listen_task:
            self._listen_task.cancel()
            self._listen_task = None
        if self.websocket:
            await self.websocket.close()
            self.websocket = None
        self.pending_responses.clear()
