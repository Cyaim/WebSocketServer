// Many tests in this assembly mutate process-global static state
// (WebSocketRouteOption.ApplicationServices, MvcChannelHandler.Clients,
// GlobalClusterCenter.*). Running collections serially guarantees no static-state
// races so the suite is deterministic. The suite runs in a couple of seconds, so
// disabling cross-collection parallelism has negligible cost.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
