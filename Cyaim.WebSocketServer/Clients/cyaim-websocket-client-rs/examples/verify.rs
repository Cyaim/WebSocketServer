/// 验证脚本，用于检查 Rust 客户端库的基本功能
/// 
/// 使用方法：
/// cargo run --example verify

use cyaim_websocket_client::{WebSocketClientFactory, WebSocketClientOptions, SerializationProtocol};

#[tokio::main]
async fn main() {
    println!("=== Rust 客户端库验证 ===\n");

    // 1. 检查 SerializationProtocol 枚举
    println!("1. 检查 SerializationProtocol 枚举...");
    let json_protocol = SerializationProtocol::Json;
    let msgpack_protocol = SerializationProtocol::MessagePack;
    println!("   ✓ JSON: {:?}", json_protocol);
    println!("   ✓ MessagePack: {:?}", msgpack_protocol);

    // 2. 检查 WebSocketClientOptions
    println!("\n2. 检查 WebSocketClientOptions...");
    let options = WebSocketClientOptions::default();
    println!("   ✓ 默认协议: {:?}", options.protocol);
    println!("   ✓ validate_all_methods: {}", options.validate_all_methods);
    println!("   ✓ lazy_load_endpoints: {}", options.lazy_load_endpoints);

    // 3. 检查 MessagePack 选项
    println!("\n3. 检查 MessagePack 选项...");
    let mut msgpack_options = WebSocketClientOptions::default();
    msgpack_options.protocol = SerializationProtocol::MessagePack;
    println!("   ✓ MessagePack 协议设置成功: {:?}", msgpack_options.protocol);

    // 4. 检查 WebSocketClientFactory
    println!("\n4. 检查 WebSocketClientFactory...");
    let factory = WebSocketClientFactory::new(
        "http://localhost:5000".to_string(),
        "/ws".to_string(),
        Some(options),
    );
    println!("   ✓ Factory 创建成功");

    // 5. 检查 WebSocketClient
    println!("\n5. 检查 WebSocketClient...");
    let _client = factory.create_client();
    println!("   ✓ Client 创建成功");

    println!("\n=== 所有检查通过！===");
    println!("\n注意：此脚本仅验证代码结构，不进行实际网络连接。");
    println!("要测试实际功能，请参考 README.md 中的示例代码。");
}

