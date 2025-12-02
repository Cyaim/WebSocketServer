import WebSocket from "ws";
import { encode, decode } from "@msgpack/msgpack";
import {
        WebSocketClientOptions,
        SerializationProtocol,
} from "./WebSocketClientOptions";

export interface MvcRequestScheme {
        id: string;
        target: string;
        body?: any;
}

export interface MvcResponseScheme {
        id: string;
        target: string;
        status: number;
        msg?: string;
        body?: any;
}

/**
 * WebSocket client for connecting to Cyaim.WebSocketServer
 * 用于连接到 Cyaim.WebSocketServer 的 WebSocket 客户端
 */
export class WebSocketClient {
        private serverUri: string;
        private channel: string;
        private options: WebSocketClientOptions;
        private webSocket: WebSocket | null = null;
        private pendingResponses: Map<
                string,
                {
                        resolve: (value: any) => void;
                        reject: (error: Error) => void;
                }
        > = new Map();

        /**
         * Constructor / 构造函数
         */
        constructor(
                serverUri: string,
                channel: string = "/ws",
                options?: WebSocketClientOptions
        ) {
                this.serverUri = serverUri;
                this.channel = channel;
                this.options =
                        options ||
                        ({
                                protocol: SerializationProtocol.Json,
                        } as WebSocketClientOptions);
        }

        /**
         * Connect to server / 连接到服务器
         */
        async connect(): Promise<void> {
                if (
                        this.webSocket &&
                        this.webSocket.readyState === WebSocket.OPEN
                ) {
                        return;
                }

                this.disconnect();

                const uri = `${this.serverUri.replace(/\/$/, "")}${
                        this.channel
                }`;
                this.webSocket = new WebSocket(uri);

                return new Promise((resolve, reject) => {
                        this.webSocket!.on("open", () => {
                                this.webSocket!.on(
                                        "message",
                                        (data: WebSocket.Data) => {
                                                this.handleMessage(data);
                                        }
                                );
                                resolve();
                        });

                        this.webSocket!.on("error", (error) => {
                                reject(error);
                        });
                });
        }

        /**
         * Send request and wait for response / 发送请求并等待响应
         */
        async sendRequest<TRequest, TResponse>(
                target: string,
                requestBody?: TRequest
        ): Promise<TResponse> {
                if (
                        !this.webSocket ||
                        this.webSocket.readyState !== WebSocket.OPEN
                ) {
                        throw new Error(
                                "WebSocket is not connected. Call connect() first."
                        );
                }

                const requestId = this.generateId();
                const request: MvcRequestScheme = {
                        id: requestId,
                        target,
                        body: requestBody,
                };

                return new Promise<TResponse>((resolve, reject) => {
                        this.pendingResponses.set(requestId, {
                                resolve,
                                reject,
                        });

                        if (
                                this.options.protocol ===
                                SerializationProtocol.MessagePack
                        ) {
                                // 使用 MessagePack 序列化
                                const requestData = encode(request);
                                this.webSocket!.send(requestData);
                        } else {
                                // 使用 JSON 序列化
                                const requestJson = JSON.stringify(request);
                                this.webSocket!.send(requestJson);
                        }

                        // Timeout after 30 seconds
                        setTimeout(() => {
                                if (this.pendingResponses.has(requestId)) {
                                        this.pendingResponses.delete(requestId);
                                        reject(new Error("Request timeout"));
                                }
                        }, 30000);
                });
        }

        /**
         * Handle incoming message / 处理接收到的消息
         */
        private handleMessage(data: WebSocket.Data): void {
                try {
                        let response: MvcResponseScheme;

                        if (
                                this.options.protocol ===
                                SerializationProtocol.MessagePack
                        ) {
                                // 处理二进制消息（MessagePack）
                                if (
                                        data instanceof Buffer ||
                                        data instanceof Uint8Array
                                ) {
                                        response = decode(
                                                data
                                        ) as MvcResponseScheme;
                                } else if (Array.isArray(data)) {
                                        // 处理分片消息
                                        const buffer = Buffer.concat(data);
                                        response = decode(
                                                buffer
                                        ) as MvcResponseScheme;
                                } else {
                                        console.error(
                                                "Unexpected MessagePack data type:",
                                                typeof data
                                        );
                                        return;
                                }
                        } else {
                                // 处理文本消息（JSON）
                                const message = data.toString();
                                response = JSON.parse(message);
                        }

                        const pending = this.pendingResponses.get(response.id);
                        if (pending) {
                                this.pendingResponses.delete(response.id);

                                if (response.status !== 0) {
                                        pending.reject(
                                                new Error(
                                                        response.msg ||
                                                                "Unknown error"
                                                )
                                        );
                                } else {
                                        pending.resolve(response.body);
                                }
                        }
                } catch (error) {
                        console.error("Failed to parse response:", error);
                }
        }

        /**
         * Disconnect from server / 断开服务器连接
         */
        async disconnect(): Promise<void> {
                if (this.webSocket) {
                        this.webSocket.close();
                        this.webSocket = null;
                }
                this.pendingResponses.clear();
        }

        /**
         * Generate unique ID / 生成唯一 ID
         */
        private generateId(): string {
                return `${Date.now()}-${Math.random()
                        .toString(36)
                        .substr(2, 9)}`;
        }
}
