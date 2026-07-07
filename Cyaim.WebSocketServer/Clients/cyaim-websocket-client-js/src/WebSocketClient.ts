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
 * Normalize a server JSON response (PascalCase: Status/Id/Msg/Body) to the client's lowercase shape.
 * Tolerant of camelCase too. / 将服务端 PascalCase 响应归一化为客户端小写形状（同时容忍 camelCase）。
 */
function normalizeResponse(r: any): MvcResponseScheme {
        return {
                id: r.Id ?? r.id,
                target: r.Target ?? r.target,
                status: r.Status ?? r.status ?? 0,
                msg: r.Msg ?? r.msg,
                body: r.Body ?? r.body,
        };
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
                                // 服务端 MessagePackRequestScheme 使用整数 [Key(0)]Id/[Key(1)]Target/[Key(2)]Body，
                                // 即 MessagePack 数组格式，因此按 [id, target, body] 数组编码（而非命名字段的 map）。
                                // The server scheme uses integer [Key] (array format), so encode as [id, target, body].
                                const requestData = encode([
                                        request.id,
                                        request.target,
                                        request.body ?? null,
                                ]);
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
         * Stream a large payload (e.g. a file) to a streaming endpoint ([WebSocket(Stream = true)]) without
         * buffering it all in memory. The payload is framed as a binary message:
         *   [4-byte magic "\0WSU"][4-byte big-endian header length N][UTF8 JSON header {id,target,meta}][raw bytes...]
         * The response is correlated by id, exactly like sendRequest.
         * 向流式端点上传大负载（如文件），负载不整体缓冲；响应按 id 关联，用法同 sendRequest。
         *
         * @param source A Uint8Array/Buffer, a Node Readable stream, or any (async) iterable of byte chunks.
         */
        async uploadStream<TResponse = any>(
                target: string,
                source:
                        | Uint8Array
                        | NodeJS.ReadableStream
                        | AsyncIterable<Uint8Array>
                        | Iterable<Uint8Array>,
                meta?: any,
                options?: { chunkSize?: number; timeoutMs?: number }
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
                const headerBytes = Buffer.from(
                        JSON.stringify({ id: requestId, target, meta: meta ?? null }),
                        "utf8"
                );
                const prefix = Buffer.alloc(8);
                // magic "\0WSU"
                prefix[0] = 0x00;
                prefix[1] = 0x57;
                prefix[2] = 0x53;
                prefix[3] = 0x55;
                prefix.writeUInt32BE(headerBytes.length, 4);
                const chunkSize = options?.chunkSize ?? 256 * 1024;
                const timeoutMs = options?.timeoutMs ?? 300000;

                const promise = new Promise<TResponse>((resolve, reject) => {
                        this.pendingResponses.set(requestId, { resolve, reject });
                        setTimeout(() => {
                                if (this.pendingResponses.has(requestId)) {
                                        this.pendingResponses.delete(requestId);
                                        reject(new Error("Upload timeout"));
                                }
                        }, timeoutMs);
                });

                try {
                        // frame 1: magic + header-length + header — start of the fragmented binary message
                        await this.sendFrame(
                                Buffer.concat([prefix, headerBytes]),
                                false
                        );
                        // payload frames (never buffers the whole payload)
                        for await (const chunk of WebSocketClient.chunkSource(
                                source,
                                chunkSize
                        )) {
                                if (chunk.length > 0) {
                                        await this.sendFrame(chunk, false);
                                }
                        }
                        // terminator: an empty final frame closes the message
                        await this.sendFrame(Buffer.alloc(0), true);
                } catch (err) {
                        this.pendingResponses.delete(requestId);
                        throw err;
                }
                return promise;
        }

        /** Send one binary frame; resolves once the frame is written (natural backpressure). */
        private sendFrame(data: Uint8Array, fin: boolean): Promise<void> {
                return new Promise((resolve, reject) => {
                        this.webSocket!.send(
                                data,
                                { binary: true, fin },
                                (err?: Error) => (err ? reject(err) : resolve())
                        );
                });
        }

        /** Normalize any supported source into byte chunks no larger than chunkSize. */
        private static async *chunkSource(
                source: any,
                chunkSize: number
        ): AsyncGenerator<Uint8Array> {
                const emit = function* (buf: Uint8Array): Generator<Uint8Array> {
                        for (let off = 0; off < buf.length; off += chunkSize) {
                                yield buf.subarray(
                                        off,
                                        Math.min(off + chunkSize, buf.length)
                                );
                        }
                };
                if (source instanceof Uint8Array) {
                        yield* emit(source);
                        return;
                }
                if (
                        source &&
                        (typeof source[Symbol.asyncIterator] === "function" ||
                                typeof source[Symbol.iterator] === "function")
                ) {
                        for await (const part of source as AsyncIterable<any>) {
                                const buf =
                                        part instanceof Uint8Array
                                                ? part
                                                : Buffer.from(part);
                                yield* emit(buf);
                        }
                        return;
                }
                throw new Error(
                        "Unsupported upload source: expected Uint8Array, Readable stream, or (async) iterable of bytes."
                );
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
                                let buffer: Uint8Array;
                                if (
                                        data instanceof Buffer ||
                                        data instanceof Uint8Array
                                ) {
                                        buffer = data as Uint8Array;
                                } else if (Array.isArray(data)) {
                                        buffer = Buffer.concat(data);
                                } else if (data instanceof ArrayBuffer) {
                                        buffer = new Uint8Array(data);
                                } else {
                                        console.error(
                                                "Unexpected MessagePack data type:",
                                                typeof data
                                        );
                                        return;
                                }
                                // 服务端 MessagePackResponseScheme 使用整数 [Key]：
                                // [0]Status [1]Msg [2]RequestTime [3]CompleteTime [4]Id [5]Target [6]Body（数组格式）。
                                // The server response scheme uses integer [Key], decoded as an array.
                                const arr = decode(buffer) as any;
                                response = Array.isArray(arr)
                                        ? {
                                                  status: arr[0],
                                                  msg: arr[1],
                                                  id: arr[4],
                                                  target: arr[5],
                                                  body: arr[6],
                                          }
                                        : normalizeResponse(arr);
                        } else {
                                // 处理文本消息（JSON）。服务端响应为 PascalCase（Status/Id/Msg/Body）。
                                // JSON response is PascalCase; normalize to the client's lowercase shape.
                                const message = data.toString();
                                response = normalizeResponse(JSON.parse(message));
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
