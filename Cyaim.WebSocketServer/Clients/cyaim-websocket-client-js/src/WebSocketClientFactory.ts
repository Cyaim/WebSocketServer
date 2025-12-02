import { WebSocketClient } from "./WebSocketClient";
import { WebSocketClientOptions } from "./WebSocketClientOptions";
import { WebSocketEndpointInfo } from "./WebSocketEndpointInfo";

interface ApiResponse<T> {
        success: boolean;
        data?: T;
        error?: string;
}

/**
 * Factory for creating WebSocket client proxies based on server endpoints
 * 基于服务器端点创建 WebSocket 客户端代理的工厂
 */
export class WebSocketClientFactory {
        private serverBaseUrl: string;
        private channel: string;
        private cachedEndpoints: WebSocketEndpointInfo[] | null = null;
        private options: WebSocketClientOptions;

        /**
         * Constructor / 构造函数
         */
        constructor(
                serverBaseUrl: string,
                channel: string = "/ws",
                options?: WebSocketClientOptions
        ) {
                this.serverBaseUrl = serverBaseUrl.replace(/\/$/, "");
                this.channel = channel;
                this.options = options || new WebSocketClientOptions();
        }

        /**
         * Get endpoints from server / 从服务器获取端点
         */
        async getEndpoints(): Promise<WebSocketEndpointInfo[]> {
                if (this.cachedEndpoints) {
                        return this.cachedEndpoints;
                }

                try {
                        const url = `${this.serverBaseUrl}/ws_server/api/endpoints`;
                        const response = await fetch(url);

                        if (!response.ok) {
                                throw new Error(
                                        `HTTP error! status: ${response.status}`
                                );
                        }

                        const apiResponse: ApiResponse<
                                WebSocketEndpointInfo[]
                        > = await response.json();

                        if (apiResponse.success && apiResponse.data) {
                                this.cachedEndpoints = apiResponse.data;
                                return this.cachedEndpoints;
                        }

                        throw new Error(
                                apiResponse.error || "Failed to fetch endpoints"
                        );
                } catch (error) {
                        throw new Error(
                                `Error fetching WebSocket endpoints from server: ${error}`
                        );
                }
        }

        /**
         * Create a client proxy for a specific interface / 为特定接口创建客户端代理
         */
        async createClient<
                T extends Record<string, (...args: any[]) => Promise<any>>
        >(interfaceDefinition: T): Promise<T> {
                const wsUri = this.serverBaseUrl.replace(/^http/, "ws");
                const client = new WebSocketClient(
                        wsUri,
                        this.channel,
                        this.options
                );

                // Get endpoints if not lazy loading / 如果不是延迟加载，则获取端点
                let endpoints: WebSocketEndpointInfo[] | null = null;
                if (!this.options.lazyLoadEndpoints) {
                        endpoints = await this.getEndpoints();
                }

                // Create proxy / 创建代理
                return this.createProxy(client, interfaceDefinition, endpoints);
        }

        /**
         * Create proxy instance / 创建代理实例
         */
        private createProxy<
                T extends Record<string, (...args: any[]) => Promise<any>>
        >(
                client: WebSocketClient,
                interfaceDefinition: T,
                endpoints: WebSocketEndpointInfo[] | null
        ): T {
                const proxy = {} as T;

                for (const [methodName, method] of Object.entries(
                        interfaceDefinition
                )) {
                        // Find endpoint for this method / 查找此方法的端点
                        let endpoint: WebSocketEndpointInfo | null = null;

                        // Check if method has endpoint metadata / 检查方法是否有端点元数据
                        const endpointTarget = (method as any).__endpointTarget;
                        if (endpointTarget) {
                                if (endpoints) {
                                        endpoint =
                                                endpoints.find(
                                                        (ep) =>
                                                                ep.target.toLowerCase() ===
                                                                        endpointTarget.toLowerCase() ||
                                                                ep.methodPath.toLowerCase() ===
                                                                        endpointTarget.toLowerCase() ||
                                                                ep.fullName.toLowerCase() ===
                                                                        endpointTarget.toLowerCase()
                                                ) || null;
                                } else {
                                        // Lazy loading: create temporary endpoint / 延迟加载：创建临时端点
                                        endpoint = {
                                                target: endpointTarget,
                                        } as WebSocketEndpointInfo;
                                }
                        } else if (endpoints) {
                                // Try to find by method name / 尝试通过方法名查找
                                endpoint =
                                        endpoints.find(
                                                (ep) =>
                                                        ep.action.toLowerCase() ===
                                                                methodName.toLowerCase() ||
                                                        ep.methodPath.toLowerCase() ===
                                                                methodName.toLowerCase()
                                        ) || null;
                        }

                        // Create proxy method / 创建代理方法
                        proxy[methodName] = async (...args: any[]) => {
                                // Lazy load endpoint if needed / 如果需要，延迟加载端点
                                if (
                                        !endpoint &&
                                        this.options.lazyLoadEndpoints
                                ) {
                                        const allEndpoints =
                                                await this.getEndpoints();
                                        const endpointTarget = (method as any)
                                                .__endpointTarget;
                                        if (endpointTarget) {
                                                endpoint =
                                                        allEndpoints.find(
                                                                (ep) =>
                                                                        ep.target.toLowerCase() ===
                                                                                endpointTarget.toLowerCase() ||
                                                                        ep.methodPath.toLowerCase() ===
                                                                                endpointTarget.toLowerCase() ||
                                                                        ep.fullName.toLowerCase() ===
                                                                                endpointTarget.toLowerCase()
                                                        ) || null;
                                        } else {
                                                endpoint =
                                                        allEndpoints.find(
                                                                (ep) =>
                                                                        ep.action.toLowerCase() ===
                                                                                methodName.toLowerCase() ||
                                                                        ep.methodPath.toLowerCase() ===
                                                                                methodName.toLowerCase()
                                                        ) || null;
                                        }
                                }

                                if (!endpoint) {
                                        const errorMsg = `Endpoint not found for method ${methodName}. Use endpoint() decorator to specify the endpoint.`;
                                        if (
                                                this.options
                                                        .throwOnEndpointNotFound
                                        ) {
                                                throw new Error(errorMsg);
                                        } else {
                                                throw new Error(errorMsg);
                                        }
                                }

                                // Build request body / 构建请求体
                                let requestBody: any = undefined;
                                if (args.length === 1) {
                                        requestBody = args[0];
                                } else if (args.length > 1) {
                                        // Create object from parameters / 从参数创建对象
                                        const paramNames =
                                                (method as any).__paramNames ||
                                                [];
                                        requestBody = {};
                                        for (
                                                let i = 0;
                                                i < args.length &&
                                                i < paramNames.length;
                                                i++
                                        ) {
                                                requestBody[paramNames[i]] =
                                                        args[i];
                                        }
                                }

                                await client.connect();
                                return await client.sendRequest<any, any>(
                                        endpoint.target,
                                        requestBody
                                );
                        };
                }

                return proxy;
        }
}

/**
 * Decorator to specify endpoint target / 用于指定端点目标的装饰器
 */
export function endpoint(target: string) {
        return function (
                targetObj: any,
                propertyKey: string,
                descriptor: PropertyDescriptor
        ) {
                descriptor.value.__endpointTarget = target;
                return descriptor;
        };
}
