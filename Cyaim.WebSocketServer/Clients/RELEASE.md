# Client Libraries — Release Guide / 客户端库发布指南

Six client libraries are published to a **GitHub Release** (artifacts attached) and to each
language's **package registry** by the CI workflow `.github/workflows/release-clients.yml`.

六个客户端库通过 CI 工作流 `.github/workflows/release-clients.yml` 发布到 **GitHub Release**（附带产物）
以及各语言的 **包管理平台**。

| Client | Registry | Package name | Publish secret |
|--------|----------|--------------|----------------|
| C#     | [NuGet](https://nuget.org)     | `Cyaim.WebSocketServer.Client` | `NUGET_API_KEY` |
| JS/TS  | [npm](https://npmjs.com)       | `@cyaim/websocket-client`      | `NPM_TOKEN` |
| Python | [PyPI](https://pypi.org)       | `cyaim-websocket-client`       | `PYPI_TOKEN` |
| Rust   | [crates.io](https://crates.io) | `cyaim-websocket-client`       | `CARGO_TOKEN` |
| Dart   | [pub.dev](https://pub.dev)     | `cyaim_websocket_client`       | OIDC (no secret) |
| Java   | [Maven Central](https://central.sonatype.com) | `com.cyaim:websocket-client` | `OSSRH_USERNAME`/`OSSRH_PASSWORD` + GPG |

All packages have been verified to **build/pack locally** (C#, JS, Python, Rust confirmed via
`dotnet pack` / `npm pack` / `python -m build` / `cargo package`; Java via Gradle/Maven; Dart is a
valid pub package). Each has been **end-to-end verified against a running server** for both the JSON
and MessagePack protocols.

## How to release / 如何发布

1. **Configure secrets** — In the GitHub repo, add the registry tokens above under
   *Settings → Secrets and variables → Actions*. Each publish job is **skipped if its secret is
   absent**, so you can enable registries one at a time. / 在仓库 Secrets 中配置上表的令牌；
   缺失某个令牌时对应发布步骤会自动跳过，可逐个平台启用。

2. **pub.dev (Dart)** uses GitHub OIDC automated publishing — enable it on pub.dev for this repo
   (*Admin → Automated publishing*) instead of a secret. / pub.dev 用 GitHub OIDC 自动发布，在
   pub.dev 该包管理页开启即可，无需 secret。

3. **Maven Central (Java)** additionally requires GPG signing and the release profile
   (`nexus-staging-maven-plugin` + `maven-gpg-plugin` + sources/javadoc jars). Add the release
   profile to `cyaim-websocket-client-java/pom.xml` and set `MAVEN_GPG_PRIVATE_KEY` /
   `MAVEN_GPG_PASSPHRASE`. / Maven Central 还需 GPG 签名与 release profile，见 pom.xml。

4. **Bump versions** so they match across all clients (see `version` in each package manifest).

5. **Tag and push** — publishing is triggered by pushing a tag:
   ```bash
   git tag client-v2.0.0
   git push origin client-v2.0.0
   ```
   or run the workflow manually (*Actions → Release Client Libraries → Run workflow*).
   The workflow builds all packages, creates the GitHub Release with the artifacts attached, then
   publishes to every registry whose secret is configured.

> Note: publishing to public registries is an irreversible action performed under the project's
> accounts, so it runs in CI with the maintainer's secrets — it is intentionally not executed from
> a developer machine. / 发布到公共仓库不可逆且以项目账号进行，故由 CI 用维护者的 secrets 执行。
