# .gitignore 配置说明

## 概述

已为所有客户端库创建了 `.gitignore` 文件，用于排除编译和发布相关的文件。

## 各客户端库的 .gitignore

### 1. C# (.NET) - `Cyaim.WebSocketServer.Client/.gitignore`
忽略内容：
- ✅ `bin/` - 编译输出目录
- ✅ `obj/` - 中间文件目录
- ✅ `*.nupkg` - NuGet 包文件
- ✅ `*.dll`, `*.pdb` - 编译产物
- ✅ `.vs/`, `.vscode/` - IDE 配置
- ✅ 其他 Visual Studio 相关文件

### 2. TypeScript/JavaScript - `cyaim-websocket-client-js/.gitignore`
忽略内容：
- ✅ `node_modules/` - 依赖包目录
- ✅ `dist/`, `build/` - 构建输出
- ✅ `*.js`, `*.js.map` - 编译后的 JavaScript 文件
- ✅ `.vscode/`, `.idea/` - IDE 配置
- ✅ 日志和缓存文件

### 3. Rust - `cyaim-websocket-client-rs/.gitignore`
忽略内容：
- ✅ `target/` - Cargo 构建目录
- ✅ `*.pdb` - 调试符号文件
- ✅ `.idea/`, `.vscode/` - IDE 配置
- ⚠️ `Cargo.lock` - **已注释**，建议提交（库项目应锁定依赖版本）

### 4. Java - `cyaim-websocket-client-java/.gitignore`
忽略内容：
- ✅ `target/` - Maven 构建目录
- ✅ `*.class` - 编译后的类文件
- ✅ `*.jar`, `*.war` - 打包文件
- ✅ `.idea/`, `.vscode/` - IDE 配置
- ✅ Maven 相关临时文件

### 5. Dart - `cyaim-websocket-client-dart/.gitignore`
忽略内容：
- ✅ `.dart_tool/` - Dart 工具目录
- ✅ `build/` - 构建输出目录
- ✅ `.packages`, `.pub-cache/` - Pub 缓存
- ⚠️ `pubspec.lock` - **已注释**，建议提交（库项目应锁定依赖版本）
- ✅ `*.g.dart`, `*.freezed.dart` - 生成的代码文件

### 6. Python - `cyaim-websocket-client-python/.gitignore`
忽略内容：
- ✅ `__pycache__/` - Python 缓存目录
- ✅ `*.pyc`, `*.pyo` - 编译的 Python 文件
- ✅ `build/`, `dist/` - 构建和分发目录
- ✅ `*.egg-info/` - 包信息目录
- ✅ `.venv/`, `venv/` - 虚拟环境
- ✅ `.vscode/`, `.idea/` - IDE 配置

### 7. 通用 - `Clients/.gitignore`
通用的忽略规则，覆盖所有客户端库的常见构建目录和文件。

## 注意事项

### 应该提交的文件 ✅
- 源代码文件（`.cs`, `.ts`, `.rs`, `.java`, `.dart`, `.py`）
- 配置文件（`.csproj`, `package.json`, `Cargo.toml`, `pom.xml`, `pubspec.yaml`, `setup.py`）
- 文档文件（`README.md`, `*.md`）
- 依赖锁定文件（`Cargo.lock`, `pubspec.lock`）- 对于库项目建议提交

### 应该忽略的文件 ❌
- 编译输出（`bin/`, `obj/`, `target/`, `build/`, `dist/`）
- 依赖包目录（`node_modules/`, `.dart_tool/`, `__pycache__/`）
- IDE 配置（`.vs/`, `.vscode/`, `.idea/`）
- 编译产物（`*.dll`, `*.exe`, `*.class`, `*.jar`, `*.nupkg`）
- 日志和缓存文件（`*.log`, `.cache/`）

## 验证 .gitignore

可以使用以下命令验证 .gitignore 是否生效：

```bash
# 检查文件是否被忽略
git check-ignore -v <文件路径>

# 查看 Git 状态（被忽略的文件不会显示）
git status
```

## 如果需要忽略依赖锁定文件

如果您的项目策略是不提交依赖锁定文件，可以：

1. **Rust**: 取消注释 `cyaim-websocket-client-rs/.gitignore` 中的 `Cargo.lock`
2. **Dart**: 取消注释 `cyaim-websocket-client-dart/.gitignore` 中的 `pubspec.lock`

## 更新日期

2024 - 初始创建

