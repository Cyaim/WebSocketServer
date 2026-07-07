package com.cyaim.websocket;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.google.gson.Gson;
import com.google.gson.annotations.SerializedName;
import org.java_websocket.handshake.ServerHandshake;
import org.msgpack.jackson.dataformat.MessagePackFactory;

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
    private final WebSocketClientOptions options;
    private org.java_websocket.client.WebSocketClient webSocket;
    private final Map<String, CompletableFuture<MvcResponseScheme>> pendingResponses = new ConcurrentHashMap<>();
    private final Gson gson = new Gson();
    private final ObjectMapper msgpackMapper;

    public WebSocketClient(String serverUri, String channel) {
        this(serverUri, channel, new WebSocketClientOptions());
    }

    public WebSocketClient(String serverUri, String channel, WebSocketClientOptions options) {
        this.serverUri = serverUri;
        this.channel = channel;
        this.options = options;
        this.msgpackMapper = new ObjectMapper(new MessagePackFactory());
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
                    handleTextMessage(message);
                }

                @Override
                public void onMessage(java.nio.ByteBuffer bytes) {
                    byte[] byteArray = new byte[bytes.remaining()];
                    bytes.get(byteArray);
                    handleBinaryMessage(byteArray);
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

        // 根据协议选择序列化方式
        if (options.protocol == SerializationProtocol.MessagePack) {
            try {
                // 服务端 MessagePackRequestScheme 使用整数 [Key(0)]Id/[Key(1)]Target/[Key(2)]Body（数组格式），
                // 因此按 [id, target, body] 数组编码。
                // Encode as a [id, target, body] array to match the server's integer [Key] contract.
                Object[] arr = new Object[] { request.id, request.target, request.body };
                byte[] requestBytes = msgpackMapper.writeValueAsBytes(arr);
                webSocket.send(requestBytes);
            } catch (Exception e) {
                future.completeExceptionally(e);
                return future;
            }
        } else {
            String requestJson = gson.toJson(request);
            webSocket.send(requestJson);
        }

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

    /**
     * Stream a large payload (e.g. a file) to a streaming endpoint ({@code [WebSocket(Stream = true)]})
     * without buffering it all in memory. Framed as a binary message:
     * [4-byte magic "\0WSU"][4-byte big-endian header length][UTF8 JSON header {id,target,meta}][raw bytes...].
     * The response is correlated by id, like {@link #sendRequest}.
     * 向流式端点上传大负载（如文件），负载不整体缓冲；响应按 id 关联。
     */
    public <TResponse> CompletableFuture<TResponse> uploadStream(
            String target, java.io.InputStream source, Object meta, int chunkSize, Class<TResponse> responseType) {
        if (webSocket == null || !webSocket.isOpen()) {
            return CompletableFuture.failedFuture(
                new IllegalStateException("WebSocket is not connected. Call connect() first."));
        }

        String requestId = java.util.UUID.randomUUID().toString();
        CompletableFuture<TResponse> future = new CompletableFuture<>();
        pendingResponses.put(requestId, new CompletableFuture<MvcResponseScheme>() {
            @Override
            public boolean complete(MvcResponseScheme value) {
                if (value.status != 0) {
                    future.completeExceptionally(
                        new RuntimeException(value.msg != null ? value.msg : "Unknown error"));
                } else {
                    try {
                        future.complete(value.body != null
                            ? gson.fromJson(gson.toJsonTree(value.body), responseType) : null);
                    } catch (Exception e) {
                        future.completeExceptionally(e);
                    }
                }
                return super.complete(value);
            }
        });

        try {
            int size = chunkSize <= 0 ? 256 * 1024 : chunkSize;
            java.util.Map<String, Object> headerMap = new java.util.HashMap<>();
            headerMap.put("id", requestId);
            headerMap.put("target", target);
            headerMap.put("meta", meta);
            byte[] header = gson.toJson(headerMap).getBytes(java.nio.charset.StandardCharsets.UTF_8);

            // frame 1: magic + header-length (big-endian) + header
            java.nio.ByteBuffer first = java.nio.ByteBuffer.allocate(8 + header.length);
            first.put((byte) 0x00).put((byte) 'W').put((byte) 'S').put((byte) 'U');
            first.putInt(header.length);
            first.put(header);
            first.flip();
            webSocket.sendFragmentedFrame(org.java_websocket.enums.Opcode.BINARY, first, false);

            // payload frames with one-chunk read-ahead so the FINAL frame carries fin=true
            byte[] buf = new byte[size];
            byte[] next = new byte[size];
            int read = source.read(buf);
            if (read <= 0) {
                webSocket.sendFragmentedFrame(org.java_websocket.enums.Opcode.BINARY, java.nio.ByteBuffer.allocate(0), true);
            } else {
                while (true) {
                    int nextRead = source.read(next);
                    boolean isLast = nextRead <= 0;
                    webSocket.sendFragmentedFrame(org.java_websocket.enums.Opcode.BINARY,
                        java.nio.ByteBuffer.wrap(java.util.Arrays.copyOfRange(buf, 0, read)), isLast);
                    if (isLast) {
                        break;
                    }
                    byte[] tmp = buf; buf = next; next = tmp; read = nextRead;
                }
            }
        } catch (Exception e) {
            pendingResponses.remove(requestId);
            future.completeExceptionally(e);
            return future;
        }

        // Timeout after 5 minutes
        java.util.concurrent.ScheduledExecutorService scheduler =
            java.util.concurrent.Executors.newScheduledThreadPool(1);
        scheduler.schedule(() -> {
            if (pendingResponses.containsKey(requestId)) {
                pendingResponses.remove(requestId);
                future.completeExceptionally(new RuntimeException("Upload timeout"));
            }
        }, 5, java.util.concurrent.TimeUnit.MINUTES);

        return future;
    }

    private void handleTextMessage(String message) {
        if (options.protocol != SerializationProtocol.Json) {
            return;
        }
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

    private void handleBinaryMessage(byte[] bytes) {
        if (options.protocol != SerializationProtocol.MessagePack) {
            return;
        }
        try {
            // 服务端 MessagePackResponseScheme 使用整数 [Key]，解码为数组：
            // [Status(0), Msg(1), RequestTime(2), CompleteTime(3), Id(4), Target(5), Body(6)]。
            // The server response decodes to a positional array.
            Object[] arr = msgpackMapper.readValue(bytes, Object[].class);
            MvcResponseScheme response = new MvcResponseScheme();
            response.status = arr.length > 0 && arr[0] instanceof Number ? ((Number) arr[0]).intValue() : 0;
            response.msg = arr.length > 1 && arr[1] != null ? arr[1].toString() : null;
            response.id = arr.length > 4 && arr[4] != null ? arr[4].toString() : null;
            response.target = arr.length > 5 && arr[5] != null ? arr[5].toString() : null;
            response.body = arr.length > 6 ? arr[6] : null;
            CompletableFuture<MvcResponseScheme> future = pendingResponses.remove(response.id);
            if (future != null) {
                future.complete(response);
            }
        } catch (Exception e) {
            System.err.println("Failed to parse MessagePack response: " + e.getMessage());
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

    // The server serializes JSON responses in PascalCase (Status/Id/Msg/Body); map via @SerializedName.
    // 服务端 JSON 响应为 PascalCase，用 @SerializedName 映射。
    private static class MvcResponseScheme {
        @SerializedName("Id") public String id;
        @SerializedName("Target") public String target;
        @SerializedName("Status") public int status;
        @SerializedName("Msg") public String msg;
        @SerializedName("Body") public Object body;
    }
}

