# 本地网关联调

从工作区根目录运行。提供客户端配置中的 16 字节 `ServerMetaKey`，服务端在返回区服列表时用它加密。

```powershell
dotnet run --project server/Stella.LocalGateway
```

后台启动可使用 `./server/start-local.ps1`。脚本先编译，再复制到 `work/gateway-runtime` 并以 `dotnet` 启动，后台进程不会锁住 VS 的 `bin/Debug/net10.0/Stella.LocalGateway.exe`。日志位于 `work/gateway-live.log` 和 `work/gateway-live.err.log`。在 VS 启动调试前，运行 `./server/start-local.ps1 -StopOnly` 释放 1989 端口；再次执行脚本会重启后台实例。

服务端优先读取 `Stella__ServerMetaKey` 等显式配置，也支持当前 Windows 用户的 .NET User Secrets，键名是 `Stella:ServerMetaKey`。仓库不包含 User Secrets；首次部署请按根目录 README 配置。

默认监听回环地址及 `127.0.0.1:1989`，与 Xposed 的默认地址一致。换电脑或 LAN IP 时，在 [appsettings.json](appsettings.json) 调整 `Stella.ListenAddress`，并在 [serverlist.json](Fixtures/meta/serverlist.json) 更新 `Agent[].Addr`。

| 接口 | 行为 |
|---|---|
| `GET /healthz` | 检查服务状态 |
| `POST /account-api/login` | 校验本地账号，签发短期登录票据，返回该账号是否已有角色 |
| `POST /account-api/register` | 注册本地账号（只建账号，不建角色），返回新 UID |
| `POST /common/config` | 返回本地 [config](Fixtures/common/config) 中的 `Code/Msg/Data` JSON。样本里的第三方凭据已清空，日志和统计功能已关闭 |
| `GET /meta/serverlist.html` | 每次读取明文 [serverlist.json](Fixtures/meta/serverlist.json)，序列化成 Protobuf，用新 IV 做 AES-CBC/PKCS#7 加密，返回 `IV + 密文`。缺少元数据密钥时返回 503 |
| `GET /notice/noticelist.html` | 返回 SQLite 中已发布且在展示时间内的公告，按分类组织并加密为 Protobuf，含标题、渠道和正文 URL |
| `GET /notice/content/{id}.html` | 返回以 ServerMetaKey 加密的 UTF-8 HTML 正文，草稿、未来、过期或不存在的公告返回 404 |
| `POST /agent-zone-1/` | 接收最大 64 KiB 的游戏二进制帧。需配置握手密钥 |

模块浮窗只访问本地账号接口。独立测试客户端可以使用本地游戏网关验证 Proto 登录和进场。

保存 `serverlist.json` 后下一次请求就会使用新内容。

## 本地账号：登录与注册

请求体都是 JSON，字段为 `username` 和 `password`；注册可带 `registrationCode`。GM 的“注册与注册码”页可切换开放注册或必填注册码；启用后网关注册与 GM 玩家注册页执行同一策略。GM 管理员直接建号可不填注册码。

- 账号：3-32 位，字母、数字、下划线或短横线（预置的 `stella-test` 带短横线，规则因此包含它）。
- 密码：8-128 个字符，只存在 SQLite 里，使用 PBKDF2-SHA256、16 字节随机盐、210000 次迭代。
- 登录成功返回 `uid`、`token`、`expiresIn`（120 秒）和 `hasCharacter`。`hasCharacter=false` 表示这个账号还没有游戏角色，模块据此提示“请在游戏内创建角色”。
- 注册成功返回 `201` 与 `uid`、`username`、`hasCharacter=false`、`next`。不会自动登录。
- 错误：参数非法 `400`、账号已存在 `409`、账号或密码错误 `401`、请求过频 `429`、数据库不可用 `503`。错误体统一是 `{ "error": "中文提示" }`。

注册创建 `accounts` 行；使用注册码时，次数扣减与使用记录和建号在同一 SQLite 事务内提交，失败回滚。角色由游戏内 `player_reg`（1004）创建。注册完仍需登录，登录后客户端发 `1001`，服务端回 `10001 player_new_notify`，游戏才会打开创建角色界面。

接口按来源 IP 限流：登录 20 次/分钟，注册 10 次/分钟。

### 票据

登录签发的票据有效期 120 秒，只在内存里，服务端重启即失效。游戏客户端完成 IKE 后发送 `LoginReq.Official`，用该接口返回的 UID 和票据填写 `Official.Uid` 和 `Official.Token`；HTTP 头 `X-Token` 仍使用 IKE 响应签发的游戏会话标识。票据第一次成功使用时绑定到该游戏会话，同一会话可以重试。

SDK 兼容接口（`/user/quick-login`、`/user/detail`）为每个 UID 维护 12 小时会话票据。SDK 长期票据不绑定首次 IKE，允许重连建立新会话；`sdk_tickets` 只存哈希、UID、到期时间和密码版本，网关重启后仍可校验。更新前的旧版内存票据无法恢复，更新后首次需重新输入账号密码。完全没有携带票据的探测请求会退回预置测试账号；带了票据但校验失败的请求返回 `Code=401`，不会串到别的账号。

默认账号是 `stella-test`，测试密码是 `stella-local-10001`。首次启动从 [account.json](Fixtures/test/account.json) 导入 UID 10001、盐和密码哈希。`Stella:LocalLoginName` 和 `Stella:LocalLoginPassword` 可作为测试账号的初始运行时覆盖；GM 重置密码后写入密码版本，覆盖别名也改用数据库新密码。重置密码后，新版 SDK 长期票据因密码版本改变而失效，需重新输入账号密码；已经认证的游戏会话暂不主动踢下线。

## SQLite：多账号与存档

数据库默认在 `Data/stella.sqlite`，可用 `Stella__DatabasePath` 指定。启动时自动建表或迁移，迁移可重复执行：

| `user_version` | 动作 |
| ---: | --- |
| 0 | 建 `accounts` / `players` 表，写入版本 2 |
| 1 | 给 `accounts` 加 `created_at`、给 `players` 加 `updated_at`，写入版本 2 |
| 2 | 直接使用 |

网关和 GM 启动时均会幂等执行 [extensions.sql](Persistence/extensions.sql)，补建密码版本、注册策略、注册码、兑换记录与邮件表，不更改现有 `user_version=2`，也不重置原存档。

- 一个账号一行 `accounts`（UID 主键、用户名唯一、密码盐/哈希、`is_test`），一份 `players` 存档（`player_info` 为 `proto.PlayerInfo` 的二进制）。
- UID 10001 预留给测试账号，余额、角色、大厅展示、引导进度都保留；新账号从 `MAX(uid)+1`（≥10002）分配。
- 注册、创建角色、写引导进度、写展示角色都在 `BEGIN IMMEDIATE` 事务里完成：先拿写锁再读旧值，写失败回滚。遇到 `SQLITE_BUSY` 自动重试 6 次。
- 创建角色是幂等的：同一 UID 重复提交同一昵称直接返回已有存档；昵称不同则返回 `1005 Error`，避免并发建立两份存档。
- 新角色以 [player-info.json](Fixtures/test/player-info.json) 为模板：角色 103/111/112、展示角色、初始资源（星之彩 999999）都来自模板；UID、昵称、性别、创建时间按请求覆盖。
- 新角色的引导组 1-3 预置为 `StepId=-1`，和测试账号一致，使后续登录跳过序章。建角成功回调仍无条件进入本地序章；该路径不发送 4601，通关剧情结束以 1015 上报组 1 完成，仍待实机核验（见 [PROTOCOL_BACKLOG.md](PROTOCOL_BACKLOG.md)）。

日志会遮盖 `Password`、`token`、`Authorization`、Cookie 等字段；数据库只保存盐和哈希，不保存明文密码。票据和握手密钥只在内存中，不写日志。

## 游戏网关

客户端已定义、网关尚未接入的消息见 [待修复协议清单](PROTOCOL_BACKLOG.md)；按功能模块分组、含功能级缺口的完整清单见 [未实现的功能与协议清单](PROTOCOL_GAPS.md)。

游戏网关使用 `Stella:HandshakeKey`（32 字节 UTF-8）。握手密钥和区服列表的 16 字节 `ServerMetaKey` 分开配置。本地账号接口可以独立测试，只需启动服务。

| 请求 | 成功响应 | 本地行为 |
|---|---|---|
| `1 IKEReq` | `2 IKEResp` | P-256 ECDH、HKDF-SHA256，签发短期本地会话 |
| `4 LoginReq` | `5 LoginResp` | `Account` 校验本地账号密码；`Official` 校验本地签发的 UID 和票据。无效凭据返回 `6 Error` |
| `1001 Nil` | `1002 PlayerInfo` / `10001 player_new_notify` | 有存档时读 SQLite 并填当前服务器时间；没有存档时通知客户端新建角色 |
| `1004 PlayerReg` | `1002 PlayerInfo` / `1005 Error` | 创建角色并落库，回完整存档。昵称 1-14 字、≤21 字节、不能以数字开头 |
| `1015 NewbieInfo` | `1016 Nil` / `1017 Error` | 引导进度写回 `Acc.Newbies` |
| `1021 PlayerBoardSetReq` | `1022 Nil` / `1023 Error` | 大厅展示角色写回 `PlayerInfo.Board` |
| `1006 / 1009 / 1027` | `1007 / 1010 / 1028` | 改名、当前头像保存、签名，包含各自的失败回包 |
| `1033 / 1036 / 1057` | `1034 / 1037 / 1058` | 展示成员、已拥有皮肤保存、当前头像列表；完整头像解锁台账待补 |
| `9001 / 9004 / 9007 / 9010 / 9013` | `9002 / 9005 / 9008 / 9011 / 9014` | 邮件列表、已读、领取、删除、置顶，包含各自的失败回包 |
| `1301 Nil` | `1302 TowerGrowthDetailResp` | 星塔入口：返回空的成长节点列表（新账号初始状态） |
| `4916 Nil` | `4917 NPCAffinityBookGetResp` | 星塔入口：返回 0 条 NPC 好感 |
| `1012 Ping` | `1013 Pong` | 返回服务器时间 |
| `4722 Nil` | `4723 PotentialPreselectionList` | 返回空的潜能预选列表 |
| `5119 Nil` | `5120 MallPackageList` | 返回空的商城礼包列表 |
| `5319 UI32` | `5320 SoldierSeasonInfo` | 返回所请求赛季的零进度 |
| `5401 UI32` | `5402 Bytes` | 返回空的科技解锁位图 |
| `6004 Nil` | `6005 GachaInformationResp` | 返回空的招募卡池进度列表 |
| `6022 Nil` | `6023 GachaNewbieInfoResp` | 返回空的新手招募记录 |
| `8419 Nil` | `8420 GuideMissionInfo` | 返回测试引导任务状态（第 1 章已完成，硬编码，待按存档生成） |
| `9101 Nil` | `9102 ActivityResp` | 返回空的活动详情列表 |
| `9201 Nil` | `9202 PhoneContactsInfoResp` | 返回大厅预加载用的空通讯录 |
| `11113 Nil` | `11114 ScoreBossInfo` | 返回客户端配置表中的测试赛季 13 |

### 1004 的成功回包

客户端没有 `player_reg_succeed_ack`，只有 `1004` 请求和 `1005` 失败。`HttpNetHandler.DispatchMsg` 要求回包消息号在 `MsgName` 里可解码，且只有非 `Error` 才会触发 `SendMsg` 回调；`CreatePlayerCtrl` 回调后立即读 `PlayerData.Base._nPlayerId`。只有 `1002 PlayerInfo` 能在同一包里补齐 UID、昵称、展示角色、引导进度和资源，所以 1004 成功时回 `1002`。这是客户端代码层面的唯一自洽解，真实服务端消息号仍待实机抓包核验。

### 个人信息页与 GM 邮件

个人信息页 `FriendInfoCtrl:RefreshActor` 直接访问 `CharacterSkin[Acc.SkinId].CharId`，旧模板缺少 SkinId 会让客户端在本地报错，未必再发协议。新模板已补 10301；网关和 GM 读取旧存档时将缺失或无效的皮肤修复为玩家已拥有的有效皮肤，下次存档写入时持久化。客户端需重新登录拿到修复后的 1002。

GM 管理台见 [gm/README.md](../../gm/README.md)，默认 [http://127.0.0.1:8787](http://127.0.0.1:8787)。可查询物品中文名/ID并下发单角色邮件，附件目前支持资源（Type 1）与普通背包道具（Type 2）。领取时邮件状态、资源和道具入账同事务提交，9008 的 `ChangeInfo.Props` 使用 Any 包装增量；重复领取不会重复发奖。已过期、未读/未领奖、置顶邮件按规则限制领取和删除，ID 0 支持批量领取/删除。GM 不发异步通知，发信后打开邮箱刷新可见。

当前统计：1171 个消息号中已接入 112 个，剩余 1059 个；42 个请求已处理，307 个请求未接入。运行 `python server/Stella.LocalGateway/verify_protocol_gaps.py` 可核验。

### 好友

`1201→1202` 返回好友与申请列表，空账号也返回可解码列表；UID/昵称搜索、申请、同意/全部同意、删除/拒绝申请、星标、赠领体力与推荐等共 12 请求均已接入并带失败回包。关系、申请、星标和每日赠领记录存 SQLite；双方关系在同一事务中建立/删除，搜索与列表使用对方真实存档。

限制来自客户端 Config：好友最多 50、入站申请最多 30，每好友每日赠 1 体力、日收最多 30，北京时间 05:00 换日。领取与玩家 Energy 快照在同一事务提交，响应的 ChangeInfo 包含 proto.Energy；重复/并发领取不会重复入账。初始称号缺失时按配置补前缀 1、后缀 2，避免好友列表和名片直接读取 Title[0]。

在独立测试库运行 Probe `friends` 后，重启测试网关并运行 `friends-verify` 可验证好友和 SDK 票据持久化。异步 `10010/10012` 通知及 `9540` 宿舍好友列表仍未接入，收到申请、被同意等对方变化需刷新好友列表。

### 公告

GM 底部“公告管理”可创建、编辑、预览、定时发布、下架和删除公告。`game_notices` 与 GM 共用 SQLite，保存后网关下次请求即读取新内容，无需重启。标题与纯文本正文会转义成 HTML 并保留换行；列表及正文均使用 `ServerMetaKey` 的 AES-CBC 信封（16 字节 IV + 密文），响应禁用缓存，正文 URL 含版本参数。

客户端 `GameAnnouncementData` 会过滤空正文地址和不匹配的 Channel，且只在 2–4 个非空分类时显示公告。因此预置系统/活动两条常驻公告，可编辑标题、正文、排序，不能删除、下架或设到期；其余公告可自由管理。渠道包含当前版本的官方、B 服和其他客户端配置编号。客户端更新公告需关闭后重新打开面板，手机展示仍需实机复测；未新增 `notice_change_notify`。

验证命令：先 `dotnet build server/Stella.LocalGateway -o work/notice-check -p:UseAppHost=false`，再在 `gm` 目录运行 `npm run test:notices`。测试自动启动两个独立进程和临时数据库，覆盖加密列表/正文、发布、排序、中文与转义、展示时间、常驻公告保护和删除。

握手使用 AES-GCM 和握手置乱。握手后的包使用 ChaCha20-Poly1305 和 `X-Token` 会话头，客户端发送的明文按大端排列为 `服务器时间戳（8B）+ 序号（2B）+ 消息号（2B）+ Protobuf`。服务器响应从 2 字节大端消息号开始。已用独立测试客户端验证握手和表内消息，Android 客户端的进场过程尚待实机验证。

## 独立测试客户端

[Stella.LocalGateway.Probe](../Stella.LocalGateway.Probe) 自己实现握手、会话帧和 HTTP，不复用网关的 `GatewayFrame`，用来确认客户端能解出正确的响应号和字段。

```powershell
# 启动服务端（示例用独立数据库，避免污染 Data/stella.sqlite）
$env:Stella__HandshakeKey = 'probe-handshake-key-32-bytes-okk'
$env:Stella__DatabasePath = 'Data/probe.sqlite'
dotnet run --project server/Stella.LocalGateway

# 另开终端
$env:STELLA_BASE_URL = 'http://127.0.0.1:1989'
$env:STELLA_HANDSHAKE_KEY = 'probe-handshake-key-32-bytes-okk'
dotnet run --project server/Stella.LocalGateway.Probe -- full
# 重启服务端后再跑，校验写操作落库
dotnet run --project server/Stella.LocalGateway.Probe -- verify
# 并发：重复注册、并发创建角色、并发写展示
dotnet run --project server/Stella.LocalGateway.Probe -- race
```

`full` 覆盖：旧测试账号登录、注册成功/重复/非法/短密码、错误密码、新账号 `1001→10001`、`1004→1002`、同名重试、昵称非法、`1021`、`1015`、`4916`、`1301`、`1012`、两个账号存档隔离、旧账号进场。`verify` 在服务端重启后重新登录两个新账号，确认展示角色和引导进度仍在。`race` 用 4 个并发会话验证唯一注册、幂等建角、展示不出现混合状态。

`gm` 模式用于独立测试库，**会重置该库 UID 10001 的密码并调整注册策略**，不要指向正式玩家库。网关设测试别名 `reset-test-alias`、覆盖密码 `runtime-override-123`，GM 用 `GM_DB` 指向同一测试库；Probe 设置 `STELLA_GM_URL` 后运行 `dotnet run --project server/Stella.LocalGateway.Probe -- gm`。已通过 35 项检查，包括实际网关新旧密码、旧存档兼容、资料写回、邮件加密协议与并发发奖、双进程注册码原子兑换。

## 服务端协议日志

`Stella:TraceProtocol` 在本地配置中已启用。用 `trace` 关联同一次 HTTP 请求的日志。

- `HTTP IN/OUT` 记录路径、状态码和耗时。客户端选择区服时更新本地界面，点「进入游戏」才会发起握手。
- `BODY IN/OUT` 记录本地账号登录/注册请求与结果，`Password`、`token` 显示为 `[redacted]`。
- `BODY OUT type=ServerList` 记录区服列表加密前的内容，包括 `Agent[].Addr`、`Status` 和 `Zone`。
- `PROTO IN/OUT` 记录消息号、Protobuf 类型和脱敏后的明文 JSON。未处理的消息记录消息号和长度。

密码、令牌和内嵌数据包的内容会被遮盖，每条明文最多记录约 8,192 个字符。排查完成后可用环境变量 `Stella__TraceProtocol=false` 关闭明文和 HTTP 日志。

## 抓包验证

用户提供的 99 字节请求和 215 字节响应均已用 AES-GCM 验证标签。握手解包时先从第一个字节还原置乱，再取前 12 字节作为 nonce 和 AAD，末尾 16 字节作为认证标签。验证得到的两条明文以大端消息号开头，紧接 Protobuf 消息体。

| 方向 | 消息号 | 类型 | 已验证字段 |
|---|---:|---|---|
| 请求 | 1 | `proto.IKEReq` | `ProtoVersion=1`，公钥 65 字节，`ClientTs` 未出现 |
| 响应 | 2 | `proto.IKEResp` | `Cipher=1`，`ServerTs=1795362860`，公钥 65 字节，`Token` 108 字节 |

协议定义见 [ike.proto](../../reverse/output/protocol/proto/ike.proto)，协议索引见 [protocols.json](../../reverse/output/protocol/protocols.json)。后续会话包使用另行协商的会话密钥。

## 代码结构

- `Accounts/` 包含本地账号认证、注册、票据和 SDK 兼容路由。
- `Game/` 包含游戏会话、协议编解码和大厅回包。
- `Persistence/` 管理 SQLite 表结构、迁移、种子导入和玩家存档读写。
- `Fixtures/test/` 保存可重复导入的种子数据，`Protocol/` 保存 Protobuf 定义。
