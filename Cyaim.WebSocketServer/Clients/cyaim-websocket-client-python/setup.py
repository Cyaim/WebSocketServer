from setuptools import setup, find_packages

setup(
    name="cyaim-websocket-client",
    version="1.0.0",
    description="WebSocket client for Cyaim.WebSocketServer with automatic endpoint discovery",
    author="Cyaim Studio",
    packages=find_packages(),
    install_requires=[
        "websockets>=11.0",
        "aiohttp>=3.8.0",
        "msgpack>=1.0.0",
    ],
    python_requires=">=3.8",
)

