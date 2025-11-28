package com.cyaim.websocket;

import com.google.gson.Gson;
import com.google.gson.reflect.TypeToken;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

import java.io.IOException;
import java.lang.reflect.Type;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;

/**
 * Factory for creating WebSocket client proxies based on server endpoints
 * 基于服务器端点创建 WebSocket 客户端代理的工厂
 */
public class WebSocketClientFactory {
    private final String serverBaseUrl;
    private final String channel;
    private final WebSocketClientOptions options;
    private List<WebSocketEndpointInfo> cachedEndpoints;
    private final OkHttpClient httpClient = new OkHttpClient();
    private final Gson gson = new Gson();

    public WebSocketClientFactory(String serverBaseUrl, String channel, WebSocketClientOptions options) {
        this.serverBaseUrl = serverBaseUrl.replaceAll("/$", "");
        this.channel = channel;
        this.options = options != null ? options : new WebSocketClientOptions();
    }

    /**
     * Get endpoints from server / 从服务器获取端点
     */
    public CompletableFuture<List<WebSocketEndpointInfo>> getEndpoints() {
        if (cachedEndpoints != null) {
            return CompletableFuture.completedFuture(cachedEndpoints);
        }

        return CompletableFuture.supplyAsync(() -> {
            try {
                String url = serverBaseUrl + "/ws_server/api/endpoints";
                Request request = new Request.Builder().url(url).build();
                
                try (Response response = httpClient.newCall(request).execute()) {
                    if (!response.isSuccessful()) {
                        throw new IOException("Unexpected code: " + response);
                    }

                    String json = response.body().string();
                    Type type = new TypeToken<ApiResponse<List<WebSocketEndpointInfo>>>(){}.getType();
                    ApiResponse<List<WebSocketEndpointInfo>> apiResponse = gson.fromJson(json, type);

                    if (apiResponse.success && apiResponse.data != null) {
                        cachedEndpoints = apiResponse.data;
                        return cachedEndpoints;
                    }

                    throw new RuntimeException(apiResponse.error != null ? apiResponse.error : "Failed to fetch endpoints");
                }
            } catch (Exception e) {
                throw new RuntimeException("Error fetching WebSocket endpoints from server: " + e.getMessage(), e);
            }
        });
    }

    /**
     * Create a client / 创建客户端
     */
    public WebSocketClient createClient() {
        String wsUri = serverBaseUrl
            .replace("http://", "ws://")
            .replace("https://", "wss://");
        return new WebSocketClient(wsUri, channel);
    }

    private static class ApiResponse<T> {
        public boolean success;
        public T data;
        public String error;
    }
}

