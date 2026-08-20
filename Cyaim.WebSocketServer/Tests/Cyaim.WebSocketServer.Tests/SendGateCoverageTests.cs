using System.Text.RegularExpressions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// 结构性护栏：库里不允许存在绕过 per-socket 发送门闩的裸 <c>SendAsync</c>。
    /// A structural guard: no raw <c>SendAsync</c> in the library may bypass the per-socket send gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条以前「只是」会抛 <c>InvalidOperationException</c>（.NET 不允许同一实例有两个未完成的
    /// SendAsync）。自从发送侧按 <c>MaxSendFrameBytes</c> 分帧之后，它升级成了正确性问题：一个绕过门闩
    /// 的发送可以**插进别人正在发的多帧消息中间**，于是对端收到一条带结束标志、内容却是别人前半截的
    /// 消息——正是发送不变式要消灭的东西，只是从另一扇门进来。
    /// This used to "merely" throw InvalidOperationException (.NET forbids two outstanding SendAsync calls
    /// on one instance). Since the send path started framing by MaxSendFrameBytes it became a correctness
    /// problem: a gate-bypassing send can inject itself <b>into the middle of someone else's multi-frame
    /// message</b>, so the peer receives one that carries the end flag while holding another message's
    /// opening bytes — exactly what the invariant exists to prevent, arriving by a different door.
    /// </para>
    /// <para>
    /// 用源码扫描而不是运行时断言，是因为这类回归总是以「新加一处发送」的形式出现，而新加的那处
    /// 不会有人专门为它写并发测试。扫描能在它被写下来的那一刻就失败。
    /// This scans source rather than asserting at runtime because the regression always arrives as "one more
    /// send site", and nobody writes a concurrency test for the site they just added. A scan fails the
    /// moment it is written.
    /// </para>
    /// </remarks>
    public class SendGateCoverageTests
    {
        /// <summary>
        /// 允许直接调用 <c>SendAsync</c> 的地方，每一条都要有理由。
        /// The places allowed to call SendAsync directly, each with a reason.
        /// </summary>
        private static readonly (string File, string Reason)[] Allowed =
        {
            // 门闩自身的实现：这里就是那个唯一的写入点。
            ("Infrastructure/WebSocketManager.cs", "the gate itself lives here"),
            // 集群节点之间的传输连接，不是面向客户端的 socket，没有共享写入者。
            ("Infrastructure/Cluster/Transports/WebSocketClusterTransport.cs", "node-to-node transport, not a client socket"),
        };

        [Fact]
        public void No_library_code_sends_on_a_socket_outside_the_send_gate()
        {
            var root = FindLibraryRoot();
            var offenders = new List<string>();

            // 只扫服务端库本身。客户端、测试、示例各自独占自己的 socket，没有并发写入者，不受这条约束——
            // 约束的是「多个来源同时向同一个客户端连接写」这件事。
            // Only the server library is scanned. Clients, tests and samples each own their socket outright
            // with no concurrent writer; the constraint is about several sources writing to one client
            // connection at the same time.
            string[] scanned =
            {
                Path.Combine(root, "Cyaim.WebSocketServer"),
                Path.Combine(root, "Cyaim.WebSocketServer.MessagePack"),
            };

            var files = scanned
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories));

            foreach (var file in files)
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');

                if (relative.Contains("/obj/") || relative.Contains("/bin/"))
                {
                    continue;
                }

                if (Allowed.Any(a => relative.EndsWith(a.File, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string trimmed = line.TrimStart();

                    // 注释掉的代码不算。/ Commented-out code does not count.
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith("*"))
                    {
                        continue;
                    }

                    // 只找「在某个 WebSocket 上直接发」的形状，不误伤 WebSocketManager.SendLocalAsync
                    // 或集群传输的 SendAsync(nodeId, ...)。
                    // Match "send directly on a WebSocket", without catching WebSocketManager.SendLocalAsync
                    // or the cluster transport's SendAsync(nodeId, ...).
                    if (Regex.IsMatch(line, @"\b(webSocket|socket|ws|_webSocket)\s*(\?)?\.SendAsync\s*\("))
                    {
                        offenders.Add($"{relative}:{i + 1}: {trimmed}");
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "these sends bypass the per-socket send gate and can interleave into another message's frames:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        private static string FindLibraryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Cyaim.WebSocketServer.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
