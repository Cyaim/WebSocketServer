package com.cyaim.websocket;

/**
 * Serialization protocol for WebSocket messages
 * WebSocket 消息的序列化协议
 */
public enum SerializationProtocol {
    /**
     * JSON protocol (text messages)
     * JSON 协议（文本消息）
     */
    Json(0),

    /**
     * MessagePack protocol (binary messages)
     * MessagePack 协议（二进制消息）
     */
    MessagePack(1);

    private final int value;

    SerializationProtocol(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }
}

