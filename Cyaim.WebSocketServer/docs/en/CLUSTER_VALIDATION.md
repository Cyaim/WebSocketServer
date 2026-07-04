# Cluster Mode Validation Against Real Middleware

This document records the end-to-end validation of **every** Cyaim.WebSocketServer cluster transport against a **real Redis cluster and real RabbitMQ**, the connection-string formats, and the compatibility notes you must know for production (especially Redis Cluster). Every conclusion is backed by observed data, not placeholder code.

> Environment: 6-node Redis cluster `10.0.0.1:10001-10006` (password auth) + RabbitMQ `amqp://…:15671/`. Method: two independent library nodes (processes) connect to the same middleware; node A sends uniquely-marked real messages to node B (and reverse); the peer's `MessageReceived` payload is verified byte-for-byte against what was sent.

## 1. Summary

| Transport | Broadcast | Directed send | Reverse send | Node discovery | Redis route store | Verdict |
|---|---|---|---|---|---|---|
| **WebSocket (built-in)** | ✅ | ✅ | ✅ | static config | n/a | **Usable** (2-node E2E incl. 100KB multi-frame cross-node) |
| **StackExchangeRedis** | ✅ | ✅ | ✅ | Pub/Sub | not supported | **Usable** (real Redis cluster) |
| **FreeRedis** | ✅ | ✅ | ✅ | Pub/Sub | not supported | **Usable** (real Redis cluster, fastest start) |
| **RabbitMQ** | ✅ | ✅ | ✅ | queue binding | not supported | **Usable** (real RabbitMQ) |
| **Hybrid (Redis discovery + RabbitMQ routing)** | ✅ | ✅ | ✅ | Redis Hash | ✅ (Redis) | **Usable** (real Redis cluster + RabbitMQ, separate processes) |

> For each transport the `MessageReceived` event carried a payload identical to the sender's, e.g. `{"marker":"SND-…","data":"REALDATA:SND-…"}`.

## 2. Connection strings

**Redis — StackExchange transport** (comma-separated nodes; cluster needs `abortConnect=false`; first start ~10s to discover topology):
```
10.0.0.1:10001,…,10.0.0.1:10006,password=…,abortConnect=false,connectTimeout=5000,syncTimeout=5000
```

**Redis — FreeRedis transport / Hybrid discovery** (pipe `|` separated; each segment may carry its own password; auto-handles MOVED/ASK; starts in ~1s):
```
10.0.0.1:10001,password=…|10.0.0.1:10002,password=…|…|10.0.0.1:10006,password=…
```

**RabbitMQ / Hybrid routing** (`%2f` is the URL-encoded default vhost `/`):
```
amqp://user:pass@host:port/%2f
```

## 3. Production compatibility notes

**3.1 Node discovery on Redis Cluster (Hybrid).** Discovery must NOT rely on `KEYS`/`SCAN` wildcards: on a Redis cluster, per-node keys like `websocket:cluster:nodes:{nodeId}` are spread across hash slots on different shards, and a single connection's `SCAN` only scans its own shard — missing nodes on other shards (symptom: the earlier-started node discovers the later one but not vice-versa). This library now stores all registrations in a **single Redis hash key** `websocket:cluster:nodes` (field = nodeId, value = NodeInfo JSON). One key ⇒ one shard ⇒ `HGETALL` reliably returns every node even on Redis Cluster. Liveness is heartbeat-timestamp based (no-heartbeat-for-60s ⇒ evicted via `HDEL`), so no per-field TTL is required. Custom `IRedisService` implementations must implement `HashSetAsync/HashGetAllAsync/HashGetAsync/HashDeleteAsync`.

**3.2 RabbitMQ.Client 7.x consumer.** In RabbitMQ.Client 7.x the `ReceivedAsync` sender is the **consumer object** (in 5.x/6.x it was the `IModel/IChannel`). Use the consumer's `Channel` property (falling back to the shared channel); casting the sender to `IChannel` yields null and silently drops every message.

**3.3 Queue naming.** Redis/RabbitMQ/Hybrid transports declare a per-node queue `cluster:node:{nodeId}`. Do NOT mix different transport types on the same RabbitMQ vhost with the same node IDs — different transports declare the same queue name with different args (e.g. `x-expires`) and trigger `406 PRECONDITION_FAILED`. A cluster should use one transport with unique node IDs.

**3.4 Hybrid topology.** Each Hybrid node must run in its own process (as in real deployments). Two Hybrid nodes in one process block each other at startup over shared process-level middleware clients. Redis/RabbitMQ/StackExchange transports do not have this constraint.

**3.5 Hybrid discovery convergence.** Hybrid startup connects Redis + RabbitMQ, declares exchange/queue/bindings, starts the consumer, then discovers peers via a ~5s timer. Over a WAN RabbitMQ the first deliverable message can take seconds to tens of seconds; same-DC is far faster. `BroadcastAsync` no-ops until at least one peer is known, so poll `IsNodeConnected(peer)` before critical broadcasts.

## 4. Reproducing the validation

The `scratchpad/ClusterRealMW` harness creates two nodes per transport, exchanges real messages, and verifies delivery. Hybrid requires two separate processes (Redis for discovery, RabbitMQ for routing).

## 5. Transport selection (from measurements)

- **Same DC, few nodes, lowest latency** → built-in WebSocket.
- **Have Redis, want zero new middleware, medium volume** → FreeRedis (fast start) or StackExchangeRedis.
- **Need reliable/persistent delivery, buffering** → RabbitMQ.
- **Large scale, route by connection (know which node holds a connection)** → Hybrid (Redis discovery + connection-route store + RabbitMQ routing).

---

See also: [Cluster Transports](../zh-cn/CLUSTER_TRANSPORTS.md) · [Hybrid Cluster](HYBRID_CLUSTER.md)
