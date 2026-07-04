# Hybrid Cluster Production Deployment Checklist

For teams taking the **Hybrid transport** (Redis discovery + connection-route store + RabbitMQ routing) to production. Every item maps to a real gotcha or observed behavior from real-middleware validation. Confirm each before going live.

> Use when: large connection counts, need per-connection directed delivery (know which node holds a connection), and you already run Redis + RabbitMQ. If you only need room/channel broadcast and can tolerate drops during blips, FreeRedis is simpler.

---

## 1. Middleware prerequisites
- [ ] **Redis** reachable (standalone or cluster). On **Redis Cluster**, node discovery already uses a single hash key `websocket:cluster:nodes` (cluster-safe across shards) — just ensure the connection string lists **all shard nodes**.
- [ ] **RabbitMQ** reachable; the account has `configure/write/read` on the target vhost (declare exchange/queue, bind, consume).
- [ ] **Middleware and app on the same low-latency network.** Hybrid startup serially connects Redis + RabbitMQ, declares exchange/queue/bindings, starts the consumer, then discovers peers. Over a WAN RabbitMQ, first-message time can be seconds to tens of seconds. Keep middleware and nodes in the same DC/VPC.
- [ ] Redis / RabbitMQ are themselves **HA** (Redis cluster/sentinel, RabbitMQ mirrored/quorum queues or cluster); otherwise the middleware is a single point of failure.

## 2. Connection strings
- [ ] **Redis (FreeRedis, default for Hybrid discovery):** pipe `|` separated nodes with password: `host1:port,password=xxx|host2:port,password=xxx|...` (list all shards).
- [ ] **RabbitMQ:** `amqp://user:pass@host:port/vhost` (default vhost `/` → `%2f`).
- [ ] Inject secrets via config store / env vars — never hard-code or commit them.

## 3. Node identity & process model
- [ ] **Each Hybrid node runs in its own process/container.** Two Hybrid nodes in one process block each other at startup over shared process-level middleware clients (confirmed limitation; other transports don't have it).
- [ ] **NodeId globally unique and stable** (e.g. `host-ordinal` or injected Pod name). NodeId determines the RabbitMQ queue name `cluster:node:{NodeId}` and the Redis discovery field.
- [ ] NodeId must not change on restart (else stale queues/fields linger until the 60s no-heartbeat eviction).

## 4. Queue / vhost isolation (avoid 406 PRECONDITION_FAILED)
- [ ] **One cluster uses exactly one transport type.** Don't mix Hybrid with the standalone RabbitMQ transport on the same vhost with the same NodeIds — they declare the same queue name with different args and trigger `406 inequivalent arg`.
- [ ] Separate environments / logical clusters by **distinct RabbitMQ vhosts or Redis key prefixes**.
- [ ] If a same-named queue was previously declared with different args, delete stale queues before upgrading.

## 5. Discovery & liveness
- [ ] Understand the cadence: registration writes a Redis hash field; heartbeat every 5s; discovery polls every 5s; **60s without heartbeat ⇒ evicted**.
- [ ] Before critical broadcasts, poll `IsNodeConnected(peer)` — `BroadcastAsync` no-ops until at least one peer is discovered.
- [ ] Custom `IRedisService` implementations must implement `HashSetAsync/HashGetAsync/HashGetAllAsync/HashDeleteAsync`.

## 6. Connection routing (Hybrid's core value)
- [ ] On client connect/disconnect, confirm the library writes/removes the connection→node mapping in Redis so directed delivery is a point-to-point lookup, not a full broadcast query.
- [ ] Watch route TTL/refresh so long-lived connections don't lose their route.
- [ ] Load-test **directed delivery** (send to a specific connectionId across nodes), not just broadcast.

## 7. Capacity & scaling
- [ ] Validate on **3–5 nodes** first: directed delivery, broadcast, route cleanup and failover on node up/down.
- [ ] Scale up gradually; watch Redis hash size, RabbitMQ connection/queue counts, and discovery convergence time.
- [ ] A new node should be discovered by existing nodes within one cycle (~5s); a removed node's field/queue should be reaped and its routes cleaned.
- [ ] Size per-node client capacity (`WebSocketRouteOption.MaxConnectionLimit`) against memory/handles; back-derive node count from the target total.

## 8. Networking & timeouts
- [ ] RabbitMQ auto-recovery on (`AutomaticRecoveryEnabled`/`TopologyRecoveryEnabled`, default on); confirm it reconnects and restores queues/bindings/consumers after a broker blip.
- [ ] Reasonable Redis `connectTimeout`; StackExchange cluster mode must use `abortConnect=false`.
- [ ] Graceful shutdown: on stop the node unregisters its discovery field, sends redirect-close frames, and won't hang on an unresponsive peer (close handshake is time-bounded).

## 9. Monitoring & alerting
- [ ] Wire up the built-in OpenTelemetry metrics (connections, messages in/out, cluster forwarded/received, connected nodes, errors).
- [ ] Alert on: sudden drop in discovered nodes, RabbitMQ queue backlog, Redis connection failures, rising cross-node forward error rate, long discovery convergence.
- [ ] Keep Hybrid startup/discovery logs for diagnosing "slow start / peer not discovered".

## 10. Upgrade & rollback
- [ ] Before upgrading, run `scratchpad/ClusterRealMW` (or an equivalent two-process Hybrid send/receive check) against real middleware in staging.
- [ ] Rolling upgrade: replace nodes one at a time; after each, confirm it is discovered and can send/receive cross-node before continuing.
- [ ] Compatibility: this version changed the discovery key from per-node keys `websocket:cluster:nodes:{id}` to a single hash key `websocket:cluster:nodes` — **old and new versions cannot discover each other, so do not run them simultaneously**; cut over as a batch or isolate with distinct key prefixes.

---

## TL;DR
1. One process per node; unique, stable NodeId.
2. Middleware and nodes on the same intranet.
3. One transport type, one vhost/prefix per cluster.
4. Confirm the peer is discovered before sending; load-test directed delivery.
5. Don't run old and new versions at once (discovery key structure changed).

See also: [Cluster Validation](./CLUSTER_VALIDATION.md) · [Hybrid Cluster](./HYBRID_CLUSTER.md)
