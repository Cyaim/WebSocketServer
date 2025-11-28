from typing import List
from dataclasses import dataclass

@dataclass
class WebSocketEndpointInfo:
    """WebSocket endpoint information"""
    controller: str
    action: str
    method_path: str
    methods: List[str]
    full_name: str
    target: str

