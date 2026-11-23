# Stellasora-Api

星塔旅人本地测试网关、Vue GM 后台、Xposed 联调模块与协议分析脚本。

**服务端与 GM 可以直接从本仓库部署，不需要原始 APK、逆向缓存或游戏素材。** 手机进入游戏另需匹配版本的客户端与客户端密钥，详见 [本地文件清单](LOCAL_FILES.md)。目前实现的是部分游戏功能，不是完整游戏服务端。

## 环境

- .NET SDK **10**（本项目目标 `net10.0`）。
- Node.js **22.13+**，建议使用已验证的 Node **26**；后端使用 `node:sqlite`。
- Python **3.10+**：仅协议清单校验、可选分析工具需要。
- Git。首次还原 NuGet / npm 依赖需要联网。

## 最快启动

```powershell
git clone https://github.com/IfxRev47/Stellasora-Api.git
cd Stellasora-Api
dotnet run --project server/Stella.LocalGateway
```

网关启动后会自动创建 `server/Stella.LocalGateway/Data/stella.sqlite` 并导入演示账号。访问 `http://127.0.0.1:1989/healthz` 应得到 `{"status":"ok"}`。

另开终端：

```powershell
cd Stellasora-Api/gm
npm ci
npm run build
npm start
```

浏览器打开 **http://127.0.0.1:8787**。GM 须在网关首次建库后启动，二者共用同一份 SQLite。开发前端可用 `npm run dev`（Vite 5180，API 8787）。

演示账号：`stella-test` / `stella-local-10001`。这是公开测试凭据，不是作者的私人账号。GM 无管理员登录鉴权，保持仅监听本机，不要将 GM 端口直接开放公网。

Linux/macOS 的服务端与 GM 使用同样的 `dotnet` / `npm` 命令；Windows 专用后台脚本不是必需步骤。

## 手机联调配置

仅运行账号 API 和 GM 不需要游戏密钥。游戏握手、区服列表、公告需要使用者自行提供与客户端匹配的两把密钥，仓库没有包含实际客户端密钥：

```powershell
dotnet user-secrets set "Stella:ServerMetaKey" "<匹配客户端的16字节密钥>" --project server/Stella.LocalGateway
dotnet user-secrets set "Stella:HandshakeKey" "<匹配客户端的32字节密钥>" --project server/Stella.LocalGateway
```

长度按 UTF-8 字节计算；上面的占位符必须替换。修改配置后重启网关。

手机与电脑在同一局域网时，还要配置三个位置：

1. `server/Stella.LocalGateway/appsettings.json` 的 `Stella.ListenAddress` 改为电脑实际 LAN IPv4，默认端口 1989。
2. `server/Stella.LocalGateway/Fixtures/meta/serverlist.json` 中 `Agent[0].Addr` 改为 `http://电脑LAN地址:1989/agent-zone-1/`。
3. Xposed 模块浮窗长按修改服务器地址为 `http://电脑LAN地址:1989`，允许系统防火墙放行 TCP 1989。手机上的 `127.0.0.1` 指手机自身。

不要提交配置中的真实密钥。User Secrets 保存在当前用户目录，不随仓库发布。

## 当前功能

- 本地多账号注册、密码登录、建角、个人资料与展示修改。
- SDK 重登票据、好友搜索/申请/同意/删除/星标与赠领体力。
- 邮件发放与领取、资源及普通道具附件，防止重复领取。
- GM 底部分类导航：账号、存档、邮件、物品、注册、公告。
- 注册码管理、物品中文查询、公告编辑与加密正文。

完整已实现/未实现情况见 [协议 backlog](server/Stella.LocalGateway/PROTOCOL_BACKLOG.md) 和 [协议缺口清单](server/Stella.LocalGateway/PROTOCOL_GAPS.md)。未实现请求可能返回 HTTP 400，按真实客户端日志逐项补齐。

## 代码与脚本

| 路径 | 用途 |
| --- | --- |
| `server/Stella.LocalGateway/` | ASP.NET 网关、账号认证、SQLite、协议定义、运行所需 Fixtures |
| `server/Stella.LocalGateway.Probe/` | 独立加密协议测试客户端，支持 full / verify / race / gm / friends / friends-verify |
| `server/start-local.ps1` | Windows 后台启动/停止；复制到独立运行目录以避免锁住 VS 编译输出 |
| `gm/` | Vue + Node 本地 GM，含 package-lock.json |
| `reverse/output/protocol/protocols.json` | 协议索引，供缺口校验使用 |
| `reverse/tools/` | 可选的归档、Lua、元数据与协议分析脚本 |
| `capture-wireguard.ps1` | 可选 mitmweb/WireGuard 抓包启动脚本 |
| `xposed/` | Android 联调模块源码，构建依赖见本地文件清单 |

## 验证与常见问题

```powershell
dotnet build server/Stella.LocalGateway
dotnet build server/Stella.LocalGateway.Probe
python server/Stella.LocalGateway/verify_protocol_gaps.py
```

公告端到端测试（自动使用临时库，不操作现有玩家库）：

```powershell
dotnet build server/Stella.LocalGateway -o work/notice-check -p:UseAppHost=false
cd gm
npm run test:notices
```

- **缺密钥**：游戏/区服/公告接口返回 503 是正常的配置提示；健康检查、账号 API 与 GM 仍可使用。
- **数据库不存在**：先运行网关，再启动 GM。换库时网关 `Stella__DatabasePath` 与 GM `GM_DB` 必须指向同一绝对路径。
- **EXE 被锁**：停止正在运行的网关再编译；Windows 可用 `./server/start-local.ps1` 后台启动，调试前用 `./server/start-local.ps1 -StopOnly` 释放端口。
- **旧票据失效**：旧版内存票据不能跨升级迁移，首次需输入账号密码；新版 SDK 长期票据可跨重登与服务重启。
- **测试会改数据**：Probe 的 full / gm / friends 等模式只应连接独立测试库。gm 模式会重置测试库演示账号的密码。

更多说明：[网关 README](server/Stella.LocalGateway/README.md)、[GM README](gm/README.md)、[本地文件清单](LOCAL_FILES.md)。仓库未附原始游戏程序、玩家库、实际密钥或抓包记录。
