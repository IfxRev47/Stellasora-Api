# 本地文件清单

## 一、部署网关与 GM：仓库已提供

以下文件不需要向原作者索取，`git clone` 后即可得到：

| 文件/目录 | 作用 |
| --- | --- |
| `server/Stella.LocalGateway/*.cs`、`Accounts/`、`Game/`、`Persistence/` | 网关实现与建库逻辑，含 `extensions.sql` |
| `server/Stella.LocalGateway/Protocol/*.proto` | 编译时生成 C# 协议类，NuGet 的 Grpc.Tools 处理 |
| `server/Stella.LocalGateway/Fixtures/test/account.json` | 演示账号盐/哈希，公开测试密码见 README |
| `server/Stella.LocalGateway/Fixtures/test/player-info.json` | 新角色模板 |
| `server/Stella.LocalGateway/Fixtures/catalog.json` | 物品、头像、皮肤目录；运行时直接使用，不需重新解包游戏 |
| `server/Stella.LocalGateway/Fixtures/common/config` | 清理第三方凭据后的 SDK 配置模板 |
| `server/Stella.LocalGateway/Fixtures/meta/serverlist.json` | 区服地址配置；发布默认回环地址 |
| `server/Stella.LocalGateway/appsettings*.json` | 非敏感默认配置 |
| `gm/server/`、`gm/src/`、`gm/package.json`、`gm/package-lock.json`、`gm/vite.config.js` | GM 源码及依赖锁文件 |
| `reverse/output/protocol/protocols.json` | 1171 个消息号索引，校验清单时使用 |

**运行服务端不需要** `StellaSora.apk`、`lib/fix.so`、`cs/il2cpp.cs`、`Hotfix.dll`、Lua 源码、完整游戏表、Android SDK 或 Dobby。

## 二、首次运行自动生成：不上传

| 路径 | 产生方式 |
| --- | --- |
| `server/Stella.LocalGateway/Data/stella.sqlite` 及 WAL/SHM | 网关启动自动建库；包含账号、密码哈希、角色、邮件、好友及票据哈希 |
| `server/**/bin/`、`server/**/obj/` | `dotnet build/run` |
| `gm/node_modules/` | `npm ci` |
| `gm/dist/` | `npm run build` |
| `work/` | Windows 后台运行目录、日志、测试状态等 |
| 当前用户 .NET User Secrets | 使用者自行配置实际客户端密钥 |

迁移已有环境时，应停写后备份 SQLite，或使用 SQLite backup API；不要只复制正在写入的 `.sqlite` 主文件而忽略 WAL。备份属于私人数据，不能推送公共仓库。

## 三、手机联调：使用者自行准备

| 本地输入 | 要求 |
| --- | --- |
| 游戏 APK 与游戏资源 | 与当前协议/偏移匹配的客户端；本工作区基于 `2.0.0.149` 分析，其他版本不保证兼容 |
| `Stella:ServerMetaKey` | 客户端对应的 16 字节 UTF-8 元数据密钥 |
| `Stella:HandshakeKey` | 客户端对应的 32 字节 UTF-8 握手密钥 |
| Android 设备与 Xposed/LSPosed 环境 | 安装模块并启用正确的游戏作用域；设备能访问电脑 LAN 地址 |

密钥不要写进 git。随机测试密钥只能用于 Probe 与测试网关配对，不能代替实际客户端密钥。

## 四、编译 Xposed：额外依赖，不影响后端部署

- Android SDK Platform **37**、对应构建工具、NDK、CMake **3.22.1**。
- 当前 Gradle 工具链配置指定 **JDK 25**；Gradle wrapper 9.6.0、AGP 9.4.1 已在源码中固定。
- 本地 `xposed/local.properties` 设置 `sdk.dir`，该文件不上传。
- 自行提供/构建 Dobby：
  - `xposed/app/src/main/cpp/Dobby/dobby.h`
  - `xposed/app/src/main/cpp/Dobby/arm64-v8a/libdobby.a`
- 项目当前只编译 arm64-v8a。上面两个 Dobby 文件缺失时，Android 模块不能编译；后端和 GM 不受影响。
- Dobby 上游为 https://github.com/jmpews/Dobby ，本地依赖说明固定过提交 `0932d69c320e786672361ab53825ba8f4245e9d3`；构建选项见 `xposed/app/src/main/cpp/Dobby/README.md`。预编译静态库未随仓库分发。

依赖齐全后：`cd xposed`，Windows 用 `./gradlew.bat :app:assembleDebug`，其他系统用 `./gradlew :app:assembleDebug`。本次发布验证范围为网关与 GM；未重新验证干净 Android 构建。

## 五、重跑分析脚本：仅开发分析时需要

原始输入和反编译输出未上传，按实际任务准备：

| 操作 | 需要的本地文件 |
| --- | --- |
| Lua 解包 | `reverse/inputs/lua.arcx`，与版本匹配的环境变量 `STELLA_ARCHIVE_KEY` |
| 配置表导出 | `reverse/inputs/data.arcx`、`reverse/output/lua_bytecode/_data/3a32d806ba986cc3.pb`，以及匹配的归档密钥 |
| Lua 反编译 | 解包后的 `.luac`、自行取得的 unluac JAR（通过 `--jar` 指定）、Java |
| 原始协议描述符校验 | `reverse/output/lua_bytecode/_data/1a70da105b39911e.pb`、`reverse/output/lua_source/GameCore/Network/NetMsgId.lua` |
| Hotfix 恢复 | `dumpmem/Hotfix.dll`、匹配的 `lib/fix.so`；恢复脚本带版本哈希/偏移限制 |
| 元数据容器处理 | 与命令匹配的设备原始转储文件；用脚本 `--help` 查看参数 |
| 区服列表解码 | 捕获的二进制列表（`--raw`）和 `STELLA_META_KEY` 或 `--key` |
| mitmweb 握手查看 | mitmproxy、设备抓包配置、可选 `STELLA_HANDSHAKE_KEY` |

Python 分析依赖见 `reverse/requirements.txt`。`verify_protocol_gaps.py` 仅使用标准库和仓库内的协议索引，不需要上述原始文件。部分历史 Markdown 引用本地 Lua 取证路径，这些路径属于可选分析资料，不是运行依赖。
