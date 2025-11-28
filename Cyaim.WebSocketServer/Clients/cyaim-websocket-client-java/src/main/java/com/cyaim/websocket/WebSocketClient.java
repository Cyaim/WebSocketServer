package com.cyaim.websocket;

import com.google.gson.Gson;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import org.java_websocket.client.WebSocketClient;
import org.java_websocket.handshake.ServerHandshake;

import java.net.URI;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;

/**
 * WebSocket client for connecting to Cyaim.WebSocketServer
 * 用于连接到 Cyaim.WebSocketServer 的 WebSocket 客户端
 */
public class WebSocketClient {
    private final String serverUri;
    private final String channel;
    private org.java_websocket.client.WebSocketClient webSocket;
    private final Map<String, CompletableFuture<MvcResponseScheme>> pendingResponses = new ConcurrentHashMap<>();
    private final Gson gson = new Gson();

    public WebSocketClient(String serverUri, String channel) {
        this.serverUri = serverUri;
        this.channel = channel;
    }

    /**
     * Connect to server / 连接到服务器
     */
    public CompletableFuture<Void> connect() {
        CompletableFuture<Void> future = new CompletableFuture<>();
        
        try {
            String uri = serverUri.replaceAll("/$", "") + channel;
            this.webSocket = new org.java_websocket.client.WebSocketClient(new URI(uri)) {
                @Override
                public void onOpen(ServerHandshake handshake) {
                    future.complete(null);
                }

                @Override
                public void onMessage(String message) {
                    handleMessage(message);
                }

                @Override
                public void onClose(int code, String reason, boolean remote) {
                    // Handle close
                }

                @Override
                public void onError(Exception ex) {
                    future.completeExceptionally(ex);
                }
            };
            
            webSocket.connect();
        } catch (Exception e) {
            future.completeExceptionally(e);
        }
        
        return future;
    }

    /**
     * Send request and wait for response / 发送请求并等待响应
     */
    public <TRequest, TResponse> CompletableFuture<TResponse> sendRequest(
            String target, TRequest requestBody, Class<TResponse> responseType) {
        if (webSocket == null || !webSocket.isOpen()) {
            return CompletableFuture.failedFuture(
                new IllegalStateException("WebSocket is not connected. Call connect() first."));
        }

        String requestId = java.util.UUID.randomUUID().toString();
        MvcRequestScheme request = new MvcRequestScheme();
        request.id = requestId;
        request.target = target;
        request.body = requestBody;

        CompletableFuture<TResponse> future = new CompletableFuture<>();
        pendingResponses.put(requestId, new CompletableFuture<MvcResponseScheme>() {
            @Override
            public boolean complete(MvcResponseScheme value) {
                if (value.status != 0) {
                    future.completeExceptionally(
                        new RuntimeException(value.msg != null ? value.msg : "Unknown error"));
                } else {
                    try {
                        if (value.body != null) {
                            TResponse response = gson.fromJson(gson.toJsonTree(value.body), responseType);
                            future.complete(response);
                        } else {
                            future.complete(null);
                        }
                    } catch (Exception e) {
                        future.completeExceptionally(e);
                    }
                }
                return super.complete(value);
            }
        });

        String requestJson = gson.toJson(request);
        webSocket.send(requestJson);

        // Timeout after 30 seconds
        java.util.concurrent.ScheduledExecutorService scheduler = 
            java.util.concurrent.Executors.newScheduledThreadPool(1);
        scheduler.schedule(() -> {
            if (pendingResponses.containsKey(requestId)) {
                pendingResponses.remove(requestId);
                future.completeExceptionally(new RuntimeException("Request timeout"));
            }
        }, 30, java.util.concurrent.TimeUnit.SECONDS);

        return future;
    }

    private void handleMessage(String message) {
        try {
            MvcResponseScheme response = gson.fromJson(message, MvcResponseScheme.class);
            CompletableFuture<MvcResponseScheme> future = pendingResponses.remove(response.id);
            if (future != null) {
                future.complete(response);
            }
        } catch (Exception e) {
            System.err.println("Failed to parse response: " + e.getMessage());
        }
    }

    /**
     * Disconnect from server / 断开服务器连接
     */
    public void disconnect() {
        if (webSocket != null) {
            webSocket.close();
            webSocket = null;
        }
        pendingResponses.clear();
    }

    private static class MvcRequestScheme {
        public String id;
        public String target;
        public Object body;
    }

    private static class MvcResponseScheme {
        public String id;
        public String target;
        public int status;
        public String msg;
        public JsonElement body;
    }
}

