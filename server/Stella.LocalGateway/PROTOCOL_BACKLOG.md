# 本地网关待修复协议

依据 [客户端协议索引](../../reverse/output/protocol/protocols.json)、[Lua 编号表](../../reverse/output/lua_source/GameCore/Network/NetMsgId.lua) 和 [网关实现](Game/LocalGameGateway.cs)。本清单统计游戏 Protobuf 协议。

- 客户端共定义 **1171** 个消息号，其中 **349** 个是请求。
- 本地网关已处理 **42** 个请求，待实现 **307** 个请求。计入响应和通知后，当前共缺 **1059** 个消息号（已实现 112 个：42 请求、41 成功应答、28 失败应答、1 通知）。
- 完整缺口清单（按功能模块分组、含 122 个未实现通知）见 [PROTOCOL_GAPS.md](PROTOCOL_GAPS.md)。
- 最近一次更新：2026-11-23。本批新增登录后的 1004 / 1015 / 1021 / 1301 / 4916，并补齐 1001 的新角色分支。

## 本批新增：个人信息、邮件与 GM

后续更新：GM 增加底部六类导航（账号、存档、邮件、物品、注册、公告），把资料、存档与邮件入口分开。公告从四个空分类改为真实标题/渠道/正文 URL，新增加密正文路由及 GM 发布管理，使用 `game_notices` 持久化；独立临时库的跨进程公告端到端测试通过。公告走元数据 HTTP 接口，不改变游戏消息号统计，`10044 notice_change_notify` 仍未接入。后台网关改从独立运行目录启动，避免锁住 VS 编译输出，见 `server/start-local.ps1`。

- 修复个人信息页：客户端 `FriendInfoCtrl:RefreshActor` 直接读取 `CharacterSkin[Acc.SkinId].CharId`；已有存档缺失或无效的 SkinId 在读取时修复为已拥有的有效皮肤，新角色模板补 10301。
- 新增 `1006→1007/1008` 改名、`1009→1010/1011` 当前头像保存、`1027→1028/1029` 签名、`1033→1034/1035` 展示成员、`1036→1037/1038` 展示皮肤、`1057→1058/1059` 当前头像列表。
- 新增 `9001→9002/9003` 邮件列表、`9004→9005/9006` 已读、`9007→9008/9009` 领取、`9010→9011/9012` 删除、`9013→9014/9015` 置顶。附件入账与邮件状态同事务提交，资源/普通道具以 Any 包装的增量 ChangeInfo 返回，并发重领只发一次。
- 独立 GM 新增中文物品查询、单角色邮件下发/记录、注册码批量生成/停启/使用记录/注册策略，以及 `/register` 玩家注册页；GM 管理员直接建号可留空注册码。
- 重置密码写 `account_security.password_revision`，重置后测试账号的运行时覆盖不再遮蔽数据库新密码；已有 SDK 票据不等同于重新验证密码，需退出后用账号密码重登。
- 验证：独立测试库运行 Probe `gm` 模式，覆盖旧存档兼容、实际网关新旧密码、资料、邮件并发发奖、GM 与网关争用单次注册码；通过。手机显示仍待重登复测。

## 本批新增：好友与 SDK 重登

- 好友请求 `1201/1204/1207/1210/1213/1216/1219/1222/1225/1228/1231/1234` 及其成功、失败号已接入；SQLite 保存双向关系、入站申请、单方星标及每日赠领记录。
- 使用真实玩家资料生成搜索/推荐/好友列表；排除自己与已有好友推荐，正确返回纳秒登录时间；缺失初始称号按 Config.NewPlayerTitlePrefix=1 / Suffix=2 修复。
- 体力赠送不扣发送方体力，每好友每天赠 1 点，最多收 30 点；北京时间 05:00 换日，领取记录与 Energy 存档同事务提交，并发重复领取不重复入账。
- SDK 长期票据不再绑定首次 IKE，会在有效期内允许重登；只持久化 SHA-256 哈希、UID、到期时间及密码版本。短期 120 秒票据仍单会话绑定，未知/串号/过期票据拒绝。旧内存票据无法迁移，更新后首次需账号密码重新登录。
- Probe `friends` 验证基础好友、双账号隔离和重登；重启后 `friends-verify` 验证票据、关系、星标、赠领状态持久化。好友异步通知及宿舍好友 9540 仍未实现，需刷新好友列表获取对方变化。

## 已实现基础链路（本地测试服）

“已验证”指用 `server/Stella.LocalGateway.Probe` 独立测试客户端跑通；“待实机核验”指协议实现与客户端代码一致，但还没在手机上复测。

| 请求 | 成功响应 | 状态 | 校验规则 / 写入点 |
| --- | --- | --- | --- |
| `1 IKEReq` | `2 IKEResp` | 已验证 | P-256 ECDH + HKDF-SHA256。会话键 32B，握手包 AES-GCM 加置乱 |
| `4 LoginReq` | `5 LoginResp` / `6 Error` | 已验证 | `Account` 走 SQLite 里的 PBKDF2 哈希；`Official` 校验票据所属 UID；短期票据绑定会话，SDK 长期票据允许新 IKE 重登并跨服务重启 |
| `1001 player_data_req` | `1002 PlayerInfo` / `10001 player_new_notify` | 已验证 | 有存档回 1002 并填 `ServerTs`；无存档回 10001，客户端据此打开创建角色界面 |
| `1004 player_reg_req` | `1002 PlayerInfo` / `1005 Error` | 已验证（回包号待实机核验） | 见下节“1004 成功回包实证”。昵称 1-14 字、≤21 字节、不以数字开头；写 `accounts` 对应 UID 的 `players` 行 |
| `1015 player_learn_req` | `1016 Nil` / `1017 Error` | 已验证 | `GroupId` 1-64、`StepId` -1..10000；写 `PlayerInfo.Acc.Newbies` |
| `1021 player_board_set_req` | `1022 Nil` / `1023 Error` | 已验证 | 最多 8 个 ID、去重、ID 非 0；写 `PlayerInfo.Board` |
| `1301 tower_growth_detail_req` | `1302 TowerGrowthDetailResp` | 已验证 | 返回空 `Detail`：新账号没有已激活的成长节点，是真实初始状态 |
| `4916 npc_affinity_book_get_req` | `4917 NPCAffinityBookGetResp` | 已验证 | 返回 0 条好感、`Number=0`。客户端按 `Infos` 循环缓存，空列表不会触发好感等级计算 |
| `1012 player_ping_req` | `1013 Pong` | 已验证 | 回服务器时间 |
| 既有回包 | 4723 / 5120 / 5320 / 5402 / 6005 / 6023 / 8420 / 9102 / 9202 / 11114 | 已验证（空列表/零进度） | 这批仍只是把列表留空，业务逻辑未实现，见文末“下一批” |

### 1004 成功回包实证

客户端没有 `player_reg_succeed_ack`：`NetMsgId.lua` 只定义 `player_reg_req = 1004` 和 `player_reg_failed_ack = 1005`，`MsgName` 也没有 1004 的成功类型。因此成功回包必须复用一个客户端已知、可解码、非 `proto.Error` 的消息号：

1. `HttpNetHandler.DispatchMsg` 用 `NetMsgId.MsgName[nReceiveMsgId]` 解码，未知消息号会 `assert` 失败；只有 `bError == false` 时才调用 `SendMsg` 的回调。
2. [CreatePlayerCtrl.lua](../../reverse/output/lua_source/Game/UI/CreatePlayer/CreatePlayerCtrl.lua#L429) 的回调不带参数，随后 `timerCallback_DONE` 直接读 `PlayerData.Base._nPlayerId` 并设置 Bugly UID。
3. 只有 `1002 player_data_succeed_ack` 会在同一包里写入 `Acc.Id`、`Acc.NickName`、`Acc.Newbies`、`Board`、`Res`（[player_data_succeed_ack](../../reverse/output/lua_source/GameCore/Network/HttpNetHandler.lua#L59)）。

所以本地网关对 1004 回 `1002 PlayerInfo`。这是客户端代码层面唯一自洽的解，真实服务端的消息号仍无从取证，标记为“待实机核验”：手机上创建角色后应直接进入序章、不再回到创建页。

## 已接入但只返回初始状态（不计入本批验收）

这些回包在上一轮联调中为了不阻断大厅而接入，本地服没有对应业务数据。它们返回的是“空列表 / 零进度”，不是伪造的成功计数；但也没有实现真实逻辑，后续要按需补。

| 请求 | 回包 | 现状 | 备注 |
| --- | --- | --- | --- |
| `4722` | `4723 PotentialPreselectionList` | 空列表 | 潜能预选未做 |
| `5119` | `5120 MallPackageList` | 空列表 | 礼包未做 |
| `5319` | `5320 SoldierSeasonInfo` | 只回赛季号，零进度 | 士兵系统未做 |
| `5401` | `5402 Bytes` | 空位图 | 科技解锁未做 |
| `6004` / `6022` | `6005` / `6023` | 空卡池与新兵记录 | 招募未做 |
| `8419` | `8420 GuideMissionInfo` | **硬编码“第 1 章已完成”** | 应按独立引导任务配置、进度与领奖存档生成，不能从 `Acc.Newbies` 推导 |
| `9101` | `9102 ActivityResp` | 空活动列表 | 活动未做 |
| `9201` | `9202 PhoneContactsInfoResp` | 空通讯录 | 大厅预加载 |
| `11113` | `11114 ScoreBossInfo` | 固定赛季 13 | 客户端表里该赛季存在 |

## 下一批待补（按客户端实际调用路径排序）

| 优先级 | 协议 | 原因 |
| --- | --- | --- |
| P0 | `1004→1002→本地序章→1015(1,-1)→大厅` 实机核验 | `EnterTowerPrologue` 本地组装塔 999，不发送 4601；通关剧情结束上报 1015。组 1 预置 -1 只影响后续登录，建角后的真实卡点仍需日志定位 |
| P0 | `8419 guide_mission_info` → `8420` | 现在硬编码“第 1 章已完成”；客户端会把整章任务填成已领取，需独立存储章节任务进度与领奖状态 |
| P1 | `4601 star_tower_apply` → `4602` | 普通星塔入场，由 `EnterTower` 发送，需配合交互、续关和结算实现 |
| 已接入 | `1006/1009/1027/1033/1036/1057` | 基础资料链路完成；完整头像解锁逻辑待补 |
| P1 | `1101 item_use` / `8004 energy_extract` | 初始资源的实际消耗与提取，需要 `ChangeInfo` 回包 |
| P1 | `9540 dorm_friend_list` | 宿舍好友入口，基础好友 12 请求已接入 |
| P2 | `4919 npc_affinity_plot_reward_receive` / `1304 tower_growth_node_unlock` | 星塔入口打开后的写操作（好感剧情奖励、成长节点解锁） |
| P2 | `5113 mall_monthlyCard_list` / `5125 mall_shop_list` / `5128 mall_shop_order` | 商店与月卡 |

## 当前联调相关请求的历史记录（已并入上表）

| 流程 | 请求 → 成功响应 | 触发条件 |
| --- | --- | --- |
| `player_reg_req` | `1004` → `1002 proto.PlayerInfo` | 创建角色。[创建角色](../../reverse/output/lua_source/Game/UI/CreatePlayer/CreatePlayerCtrl.lua#L429) |
| `player_learn_req` | `1015` → `1016` | 完成引导时上报。[引导逻辑](../../reverse/output/lua_source/GameCore/Data/DataClass/PlayerGuideData.lua#L71) |
| `player_board_set_req` | `1021` → `1022` | 保存大厅展示角色。[展示设置](../../reverse/output/lua_source/GameCore/Data/DataClass/PlayerBoardData.lua#L231) |
| `npc_affinity_book_get_req` | `4916` → `4917` | 点击大厅星塔入口时读取 NPC 好感。[大厅入口](../../reverse/output/lua_source/Game/UI/MainViewEx/MainViewCtrl.lua#L2341) |
| `tower_growth_detail_req` | `1301` → `1302` | 收到星塔好感数据后读取成长数据。[大厅入口](../../reverse/output/lua_source/Game/UI/MainViewEx/MainViewCtrl.lua#L2341) |

## 未接入的请求

成功和失败响应按客户端索引中的同名后缀配对。破折号表示索引无同名响应。管理类编号计入覆盖统计。这里的接口覆盖按消息号统计，已接入的测试回包仍可能缺少真实业务逻辑。

| 请求号 | 名称 | 请求类型 | 成功响应号和类型 | 失败响应号和类型 |
| ---: | --- | --- | --- | --- |
| -1 | `sudo_req` | `proto.SudoCommand` | `-2` `proto.Nil` | `-3` `proto.Error` |
| 1018 | `player_destroy_req` | `proto.Nil` | `1019` `proto.PlayerDestroy` | `1020` `proto.Error` |
| 1024 | `player_world_class_reward_receive_req` | `proto.PlayerWorldClassRewardReq` | `1025` `proto.ChangeInfo` | `1026` `proto.Error` |
| 1030 | `player_title_edit_req` | `proto.PlayerTitleEditReq` | `1031` `proto.Nil` | `1032` `proto.Error` |
| 1039 | `player_gender_edit_req` | `proto.Nil` | `1040` `proto.Nil` | `1041` `proto.Error` |
| 1042 | `player_survey_req` | `proto.PlayerSurveyReq` | `1043` `proto.PlayerSurveyResp` | `1044` `proto.Error` |
| 1045 | `player_exit_req` | `proto.Nil` | `1046` `proto.Nil` | `1047` `proto.Error` |
| 1048 | `player_honor_edit_req` | `proto.PlayerHonorEditReq` | `1049` `proto.Nil` | `1050` `proto.Error` |
| 1051 | `player_world_class_advance_req` | `proto.Nil` | `1052` `proto.ChangeInfo` | `1053` `proto.Error` |
| 1054 | `player_music_set_req` | `proto.I64` | `1055` `proto.Nil` | `1056` `proto.Error` |
| 1101 | `item_use_req` | `proto.ItemUseReq` | `1102` `proto.ChangeInfo` | `1103` `proto.Error` |
| 1104 | `gem_convert_req` | `proto.UI32` | `1105` `proto.ChangeInfo` | `1106` `proto.Error` |
| 1107 | `item_product_req` | `proto.ItemProductReq` | `1108` `proto.ChangeInfo` | `1109` `proto.Error` |
| 1110 | `fragments_convert_req` | `proto.Nil` | `1111` `proto.ChangeInfo` | `1112` `proto.Error` |
| 1113 | `daily_shop_reward_receive_req` | `proto.Nil` | `1114` `proto.ChangeInfo` | `1115` `proto.Error` |
| 1116 | `item_quick_growth_req` | `proto.ItemGrowthReq` | `1117` `proto.ChangeInfo` | `1118` `proto.Error` |
| 1119 | `daily_mall_reward_receive_req` | `proto.Nil` | `1120` `proto.ChangeInfo` | `1121` `proto.Error` |
| 1304 | `tower_growth_node_unlock_req` | `proto.UI32` | `1305` `proto.ChangeInfo` | `1306` `proto.Error` |
| 1307 | `tower_growth_group_node_unlock_req` | `proto.UI32` | `1308` `proto.TowerGrowthGroupNodeUnlockResp` | `1309` `proto.Error` |
| 1401 | `milkout_settle_req` | `proto.MilkoutSettleReq` | `1402` `proto.ChangeInfo` | `1403` `proto.Error` |
| 1501 | `activity_gds_settle_req` | `proto.ActivityGDSSettleReq` | `1502` `proto.ChangeInfo` | `1503` `proto.Error` |
| 1601 | `activity_soldier_quest_reward_receive_req` | `proto.ActivitySoldierQuestRewardReceiveReq` | `1602` `proto.ChangeInfo` | `1603` `proto.Error` |
| 2001 | `player_formation_req` | `proto.PlayerFormationReq` | `2002` `proto.Nil` | `2003` `proto.Error` |
| 2301 | `char_upgrade_req` | `proto.CharUpgradeReq` | `2302` `proto.CharUpgradeResp` | `2303` `proto.Error` |
| 2304 | `char_advance_req` | `proto.UI32` | `2305` `proto.ChangeInfo` | `2306` `proto.Error` |
| 2307 | `char_skill_upgrade_req` | `proto.CharSkillUpgradeReq` | `2308` `proto.ChangeInfo` | `2309` `proto.Error` |
| 2313 | `char_advance_reward_receive_req` | `proto.CharAdvanceRewardReceiveReq` | `2314` `proto.CharAdvanceRewardReceiveResp` | `2315` `proto.Error` |
| 2316 | `char_skin_set_req` | `proto.CharSkinSetReq` | `2317` `proto.Nil` | `2318` `proto.Error` |
| 2322 | `char_affinity_quest_reward_receive_req` | `proto.CharAffinityQuestRewardReceiveReq` | `2323` `proto.CharAffinityQuestRewardReceiveResp` | `2324` `proto.Error` |
| 2325 | `char_recruitment_req` | `proto.UI32` | `2326` `proto.ChangeInfo` | `2327` `proto.Error` |
| 2328 | `char_affinity_gift_send_req` | `proto.CharAffinityGiftSendReq` | `2329` `proto.CharAffinityGiftSendResp` | `2330` `proto.Error` |
| 2401 | `char_dating_landmark_select_req` | `proto.CharDatingLandmarkSelectReq` | `2402` `proto.CharDatingLandmarkSelectResp` | `2403` `proto.Error` |
| 2404 | `char_dating_gift_send_req` | `proto.CharDatingGiftSendReq` | `2405` `proto.CharDatingGiftSendResp` | `2406` `proto.Error` |
| 2407 | `char_dating_event_reward_receive_req` | `proto.CharDatingEventRewardReceiveReq` | `2408` `proto.ChangeInfo` | `2409` `proto.Error` |
| 2410 | `char_archive_reward_receive_req` | `proto.CharArchiveRewardReceiveReq` | `2411` `proto.ChangeInfo` | `2412` `proto.Error` |
| 2413 | `char_dating_branch_a_select_req` | `proto.CharDatingBranchASelectReq` | `2414` `proto.CharDatingBranchASelectResp` | `2415` `proto.Error` |
| 2416 | `char_dating_branch_b_select_req` | `proto.CharDatingBranchBSelectReq` | `2417` `proto.CharDatingBranchBSelectResp` | `2418` `proto.Error` |
| 2501 | `char_gem_generate_req` | `proto.CharGemGenerateReq` | `2502` `proto.CharGemGenerateResp` | `2503` `proto.Error` |
| 2504 | `char_gem_refresh_req` | `proto.CharGemRefreshReq` | `2505` `proto.CharGemRefreshResp` | `2506` `proto.Error` |
| 2507 | `char_gem_replace_attribute_req` | `proto.CharGemReplaceAttributeReq` | `2508` `proto.Nil` | `2509` `proto.Error` |
| 2510 | `char_gem_update_gem_lock_status_req` | `proto.CharGemUpdateGemLockStatusReq` | `2511` `proto.Nil` | `2512` `proto.Error` |
| 2513 | `char_gem_use_preset_req` | `proto.CharGemUsePresetReq` | `2514` `proto.Nil` | `2515` `proto.Error` |
| 2516 | `char_gem_rename_preset_req` | `proto.CharGemRenamePresetReq` | `2517` `proto.Nil` | `2518` `proto.Error` |
| 2519 | `char_gem_equip_gem_req` | `proto.CharGemEquipGemReq` | `2520` `proto.Nil` | `2521` `proto.Error` |
| 2522 | `char_gem_overlock_revert_req` | `proto.CharGemOverlockRevertReq` | `2523` `proto.ChangeInfo` | `2524` `proto.Error` |
| 2525 | `char_gem_overlock_req` | `proto.CharGemOverlockReq` | `2526` `proto.ChangeInfo` | `2527` `proto.Error` |
| 2601 | `char_favorite_set_req` | `proto.UI32` | `2602` `proto.Nil` | `2603` `proto.Error` |
| 3119 | `disc_strengthen_req` | `proto.DiscStrengthenReq` | `3120` `proto.DiscStrengthenResp` | `3121` `proto.Error` |
| 3122 | `disc_promote_req` | `proto.DiscPromoteReq` | `3123` `proto.DiscPromoteResp` | `3124` `proto.Error` |
| 3125 | `disc_limit_break_req` | `proto.DiscLimitBreakReq` | `3126` `proto.DiscLimitBreakResp` | `3127` `proto.Error` |
| 3128 | `disc_read_reward_receive_req` | `proto.DiscReadRewardReceiveReq` | `3129` `proto.ChangeInfo` | `3130` `proto.Error` |
| 3131 | `disc_all_limit_break_req` | `proto.Nil` | `3132` `proto.DiscAllLimitBreakResp` | `3133` `proto.Error` |
| 3301 | `agent_apply_req` | `proto.AgentApplyReq` | `3302` `proto.AgentApplyResp` | `3303` `proto.Error` |
| 3304 | `agent_give_up_req` | `proto.AgentGiveUpReq` | `3305` `proto.AgentGiveUpResp` | `3306` `proto.Error` |
| 3307 | `agent_reward_receive_req` | `proto.AgentRewardReceiveReq` | `3308` `proto.AgentRewardReceiveResp` | `3309` `proto.Error` |
| 4201 | `quest_tour_guide_reward_receive_req` | `proto.UI32` | `4202` `proto.TourGuideQuestRewardResp` | `4203` `proto.Error` |
| 4204 | `quest_daily_reward_receive_req` | `proto.UI32` | `4205` `proto.ChangeInfo` | `4206` `proto.Error` |
| 4207 | `dictionary_reward_receive_req` | `proto.DictRewardReq` | `4208` `proto.ChangeInfo` | `4209` `proto.Error` |
| 4210 | `quest_tower_reward_receive_req` | `proto.UI32` | `4211` `proto.ChangeInfo` | `4212` `proto.Error` |
| 4213 | `quest_daily_active_reward_receive_req` | `proto.Nil` | `4214` `proto.QuestDailyActiveRewardReceiveResp` | `4215` `proto.Error` |
| 4216 | `quest_tour_guide_group_reward_receive_req` | `proto.Nil` | `4217` `proto.TourGuideQuestGroupRewardResp` | `4218` `proto.Error` |
| 4219 | `quest_weekly_reward_receive_req` | `proto.UI32` | `4220` `proto.ChangeInfo` | `4221` `proto.Error` |
| 4222 | `quest_weekly_active_reward_receive_req` | `proto.Nil` | `4223` `proto.QuestWeeklyActiveRewardReceiveResp` | `4224` `proto.Error` |
| 4225 | `quest_assist_reward_receive_req` | `proto.AssistQuestReceiveReq` | `4226` `proto.ChangeInfo` | `4227` `proto.Error` |
| 4228 | `quest_assist_group_reward_receive_req` | `proto.UI32` | `4229` `proto.AssistGroupRewardResp` | `4230` `proto.Error` |
| 4231 | `quest_dorm_reward_receive_req` | `proto.UI32V` | `4232` `proto.QuestDormRewardReceiveSucceedAck` | `4233` `proto.Error` |
| 4301 | `activity_task_reward_receive_req` | `proto.ActivityTaskRewardReceiveReq` | `4302` `proto.ChangeInfo` | `4303` `proto.Error` |
| 4304 | `activity_task_group_reward_receive_req` | `proto.UI32` | `4305` `proto.ChangeInfo` | `4306` `proto.Error` |
| 4401 | `achievement_reward_receive_req` | `proto.AchievementRewardReq` | `4402` `proto.ChangeInfo` | `4403` `proto.Error` |
| 4404 | `achievement_info_req` | `proto.Nil` | `4405` `proto.Achievements` | `4406` `proto.Error` |
| 4501 | `infinity_tower_info_req` | `proto.Nil` | `4502` `proto.InfinityTowerInfoResp` | `4503` `proto.Error` |
| 4504 | `infinity_tower_apply_req` | `proto.InfinityTowerApplyReq` | `4505` `proto.Nil` | `4506` `proto.Error` |
| 4507 | `infinity_tower_settle_req` | `proto.InfinityTowerSettleReq` | `4508` `proto.InfinityTowerSettleResp` | `4509` `proto.Error` |
| 4510 | `infinity_tower_daily_reward_receive_req` | `proto.Nil` | `4511` `proto.InfinityTowerDailyRewardReceiveResp` | `4512` `proto.Error` |
| 4513 | `infinity_tower_plot_reward_receive_req` | `proto.UI32` | `4514` `proto.InfinityTowerPlotRewardReceiveResp` | `4515` `proto.Error` |
| 4601 | `star_tower_apply_req` | `proto.StarTowerApplyReq` | `4602` `proto.StarTowerApplyResp` | `4603` `proto.Error` |
| 4607 | `star_tower_interact_req` | `proto.StarTowerInteractReq` | `4608` `proto.StarTowerInteractResp` | `4609` `proto.Error` |
| 4610 | `star_tower_info_req` | `proto.Nil` | `4611` `proto.StarTowerInfo` | `4612` `proto.Error` |
| 4613 | `star_tower_give_up_req` | `proto.Nil` | `4614` `proto.StarTowerGiveUpResp` | `4615` `proto.Error` |
| 4701 | `star_tower_build_whether_save_req` | `proto.StarTowerBuildWhetherSaveReq` | `4702` `proto.StarTowerBuildWhetherSaveResp` | `4703` `proto.Error` |
| 4704 | `star_tower_build_brief_list_get_req` | `proto.Nil` | `4705` `proto.StarTowerBuildBriefListGetResp` | `4706` `proto.Error` |
| 4707 | `star_tower_build_detail_get_req` | `proto.StarTowerBuildDetailGetReq` | `4708` `proto.StarTowerBuildDetailGetResp` | `4709` `proto.Error` |
| 4710 | `star_tower_build_delete_req` | `proto.StarTowerBuildDeleteReq` | `4711` `proto.StarTowerBuildDeleteResp` | `4712` `proto.Error` |
| 4713 | `star_tower_build_name_set_req` | `proto.StarTowerBuildNameSetReq` | `4714` `proto.Nil` | `4715` `proto.Error` |
| 4716 | `star_tower_build_lock_unlock_req` | `proto.StarTowerBuildLockUnlockReq` | `4717` `proto.Nil` | `4718` `proto.Error` |
| 4719 | `star_tower_build_preference_set_req` | `proto.StarTowerBuildPreferenceSetReq` | `4720` `proto.Nil` | `4721` `proto.Error` |
| 4725 | `potential_preselection_import_req` | `proto.PotentialPreselectionImportReq` | `4726` `proto.PotentialPreselection` | `4727` `proto.Error` |
| 4728 | `potential_preselection_name_set_req` | `proto.PotentialPreselectionNameSetReq` | `4729` `proto.Nil` | `4730` `proto.Error` |
| 4731 | `potential_preselection_preference_set_req` | `proto.PotentialPreselectionPreferenceSetReq` | `4732` `proto.Nil` | `4733` `proto.Error` |
| 4734 | `potential_preselection_update_req` | `proto.PotentialPreselectionUpdateReq` | `4735` `proto.PotentialPreselection` | `4736` `proto.Error` |
| 4737 | `potential_preselection_delete_req` | `proto.PotentialPreselectionDeleteReq` | `4738` `proto.Nil` | `4739` `proto.Error` |
| 4740 | `tower_vita_energy_buy_req` | `proto.UI32` | `4741` `proto.TowerVitaEnergyBuyResp` | `4742` `proto.Error` |
| 4743 | `tower_vita_project_list_req` | `proto.Nil` | `4744` `proto.TowerVitaProjectListResp` | `4745` `proto.Error` |
| 4746 | `tower_vita_project_update_req` | `proto.TowerVitaProjectUpdateRep` | `4747` `proto.Nil` | `4748` `proto.Error` |
| 4749 | `tower_vita_project_name_update_req` | `proto.TowerVitaProjectNameUpdateRep` | `4750` `proto.Nil` | `4751` `proto.Error` |
| 4752 | `tower_vita_project_set_req` | `proto.TowerVitaProjectSetRep` | `4753` `proto.Nil` | `4754` `proto.Error` |
| 4755 | `star_tower_build_potential_modify_req` | `proto.StarTowerBuildPotentialModifyReq` | `4756` `proto.StarTowerBuildPotentialModifyResp` | `4757` `proto.Error` |
| 4758 | `star_tower_build_note_modify_req` | `proto.StarTowerBuildNoteModifyReq` | `4759` `proto.StarTowerBuildNoteModifyResp` | `4760` `proto.Error` |
| 4761 | `star_tower_build_generator_req` | `proto.StarTowerBuildGeneratorReq` | `4762` `proto.StarTowerBuildPotentialModifyResp` | `4763` `proto.Error` |
| 4764 | `star_tower_build_discard_req` | `proto.StarTowerBuildDiscardReq` | `4765` `proto.Nil` | `4766` `proto.Error` |
| 4801 | `build_convert_detail_list_get_req` | `proto.Nil` | `4802` `proto.BuildConvertDetailListGetResp` | `4803` `proto.Error` |
| 4804 | `build_convert_submit_req` | `proto.BuildConvertSubmitReq` | `4805` `proto.BuildConvertSubmitResp` | `4806` `proto.Error` |
| 4807 | `build_convert_group_reward_receive_req` | `proto.UI32` | `4808` `proto.BuildConvertGroupRewardReceiveResp` | `4809` `proto.Error` |
| 4901 | `star_tower_book_potential_brief_list_get_req` | `proto.Nil` | `4902` `proto.StarTowerBookPotentialBriefListResp` | `4903` `proto.Error` |
| 4904 | `star_tower_book_char_potential_get_req` | `proto.UI32` | `4905` `proto.StarTowerBookPotentialGetResp` | `4906` `proto.Error` |
| 4907 | `star_tower_book_potential_reward_receive_req` | `proto.UI32` | `4908` `proto.StarTowerBookPotentialRewardReceiveResp` | `4909` `proto.Error` |
| 4913 | `star_tower_book_event_reward_receive_req` | `proto.UI32` | `4914` `proto.StarTowerBookEventRewardReceiveResp` | `4915` `proto.Error` |
| 4919 | `npc_affinity_plot_reward_receive_req` | `proto.UI32` | `4920` `proto.NPCAffinityPlotRewardReceiveResp` | `4921` `proto.Error` |
| 5010 | `resident_shop_get_req` | `proto.ResidentShopGetReq` | `5011` `proto.ResidentShopGetResp` | `5012` `proto.Error` |
| 5013 | `resident_shop_purchase_req` | `proto.ResidentShopPurchaseReq` | `5014` `proto.ResidentShopPurchaseResp` | `5015` `proto.Error` |
| 5101 | `mall_gem_list_req` | `proto.Nil` | `5102` `proto.MallGemList` | `5103` `proto.Error` |
| 5104 | `mall_gem_order_req` | `proto.String` | `5105` `proto.OrderInfo` | `5106` `proto.Error` |
| 5107 | `mall_order_cancel_req` | `proto.String` | `5108` `proto.String` | `5109` `proto.Error` |
| 5110 | `mall_order_collect_req` | `proto.String` | `5111` `proto.CollectResp` | `5112` `proto.CollectResp` |
| 5113 | `mall_monthlyCard_list_req` | `proto.Nil` | `5114` `proto.MallMonthlyCardList` | `5115` `proto.Error` |
| 5116 | `mall_monthlyCard_order_req` | `proto.String` | `5117` `proto.OrderInfo` | `5118` `proto.Error` |
| 5122 | `mall_package_order_req` | `proto.String` | `5123` `proto.MallPackageOrder` | `5124` `proto.Error` |
| 5125 | `mall_shop_list_req` | `proto.Nil` | `5126` `proto.MallShopProductList` | `5127` `proto.Error` |
| 5128 | `mall_shop_order_req` | `proto.MallShopOrderReq` | `5129` `proto.ChangeInfo` | `5130` `proto.Error` |
| 5131 | `mall_activity_list_req` | `proto.Nil` | `5132` `proto.MallActivityList` | `5133` `proto.Error` |
| 5134 | `mall_activity_order_req` | `proto.String` | `5135` `proto.OrderInfo` | `5136` `proto.Error` |
| 5201 | `trace_hunt_info_req` | `proto.Nil` | `5202` `proto.TraceHuntInfo` | `5203` `proto.Error` |
| 5204 | `trace_hunt_apply_req` | `proto.TraceHuntApplyReq` | `5205` `proto.Nil` | `5206` `proto.Error` |
| 5207 | `trace_hunt_settle_req` | `proto.TraceHuntSettleReq` | `5208` `proto.TraceHuntSettleResp` | `5209` `proto.Error` |
| 5210 | `trace_hunt_recommend_req` | `proto.Nil` | `5211` `proto.TraceHuntRecommendResp` | `5212` `proto.Error` |
| 5213 | `trace_hunt_boss_reward_receive_req` | `proto.Nil` | `5214` `proto.TraceHuntBossRewardReceiveResp` | `5215` `proto.Error` |
| 5216 | `trace_hunt_trace_req` | `proto.I32` | `5217` `proto.TraceHuntTraceResp` | `5218` `proto.Error` |
| 5301 | `soldier_apply_req` | `proto.SoldierApplyReq` | `5302` `proto.SoldierApplyResp` | `5303` `proto.Error` |
| 5304 | `soldier_info_req` | `proto.Nil` | `5305` `proto.SoldierInfo` | `5306` `proto.Error` |
| 5307 | `soldier_interact_req` | `proto.SoldierInteractReq` | `5308` `proto.SoldierInteractResp` | `5309` `proto.Error` |
| 5310 | `soldier_give_up_req` | `proto.Nil` | `5311` `proto.SoldierSettleDataResp` | `5312` `proto.Error` |
| 5313 | `soldier_step_out_req` | `proto.Nil` | `5314` `proto.Nil` | `5315` `proto.Error` |
| 5316 | `soldier_partner_trace_req` | `proto.SoldierPartnerTraceReq` | `5317` `proto.Nil` | `5318` `proto.Error` |
| 5322 | `soldier_season_reward_receive_req` | `proto.UI32` | `5323` `proto.SoldierSeasonRewardReceiveResp` | `5324` `proto.Error` |
| 5325 | `soldier_learn_req` | `proto.SoldierLearnReq` | `5326` `proto.Nil` | `5327` `proto.Error` |
| 5404 | `soldier_technology_node_unlock_req` | `proto.UI32` | `5405` `proto.ChangeInfo` | `5406` `proto.Error` |
| 5407 | `soldier_battle_report_favorite_req` | `proto.String` | `5408` `proto.Nil` | `5409` `proto.Error` |
| 5410 | `soldier_battle_report_unfavorite_req` | `proto.String` | `5411` `proto.Nil` | `5412` `proto.Error` |
| 5413 | `soldier_battle_report_favorite_list_req` | `proto.Nil` | `5414` `proto.SoldierBattleReportFavoriteList` | `5415` `proto.Error` |
| 5416 | `soldier_battle_report_recent_list_req` | `proto.Nil` | `5417` `proto.SoldierBattleReportRecentList` | `5418` `proto.Error` |
| 6001 | `gacha_spin_req` | `proto.GachaSpinReq` | `6002` `proto.GachaSpinResp` | `6003` `proto.Error` |
| 6007 | `gacha_histories_req` | `proto.UI32` | `6008` `proto.GachaHistories` | `6009` `proto.Error` |
| 6010 | `gacha_guarantee_reward_receive_req` | `proto.UI32` | `6011` `proto.ChangeInfo` | `6012` `proto.Error` |
| 6013 | `gacha_newbie_spin_req` | `proto.UI32` | `6014` `proto.GachaNewbieSpinResp` | `6015` `proto.Error` |
| 6016 | `gacha_newbie_save_req` | `proto.GachaNewbieSaveReq` | `6017` `proto.Nil` | `6018` `proto.Error` |
| 6019 | `gacha_newbie_obtain_req` | `proto.GachaNewbieObtainReq` | `6020` `proto.ChangeInfo` | `6021` `proto.Error` |
| 6026 | `gacha_reward_get_req` | `proto.UI32` | `6027` `proto.ChangeInfo` | `6028` `proto.Error` |
| 6101 | `tower_book_fate_card_detail_req` | `proto.Nil` | `6102` `proto.TowerBookFateCardDetailResp` | `6103` `proto.Error` |
| 6104 | `tower_book_fate_card_reward_receive_req` | `proto.TowerBookFateCardRewardReq` | `6105` `proto.ChangeInfo` | `6106` `proto.Error` |
| 6201 | `joint_drill_apply_req` | `proto.JointDrillApplyReq` | `6202` `proto.JointDrillApplyResp` | `6203` `proto.Error` |
| 6204 | `joint_drill_continue_req` | `proto.JointDrillContinueReq` | `6205` `proto.Nil` | `6206` `proto.Error` |
| 6207 | `joint_drill_sync_req` | `proto.JointDrillSyncReq` | `6208` `proto.Nil` | `6209` `proto.Error` |
| 6210 | `joint_drill_give_up_req` | `proto.JointDrillGiveUpReq` | `6211` `proto.JointDrillSettle` | `6212` `proto.Error` |
| 6213 | `joint_drill_retreat_req` | `proto.Nil` | `6214` `proto.Nil` | `6215` `proto.Error` |
| 6216 | `joint_drill_settle_req` | `proto.JointDrillSettleReq` | `6217` `proto.JointDrillSettle` | `6218` `proto.Error` |
| 6219 | `joint_drill_game_over_req` | `proto.Nil` | `6220` `proto.JointDrillSettle` | `6221` `proto.Error` |
| 6222 | `joint_drill_sweep_req` | `proto.JointDrillSweepReq` | `6223` `proto.JointDrillSweepResp` | `6224` `proto.Error` |
| 6225 | `joint_drill_rank_req` | `proto.Nil` | `6226` `proto.JointDrillRankInfo` | `6227` `proto.Error` |
| 6228 | `joint_drill_quest_reward_receive_req` | `proto.Nil` | `6229` `proto.ChangeInfo` | `6230` `proto.Error` |
| 6301 | `joint_drill_2_apply_req` | `proto.JointDrill2ApplyReq` | `6302` `proto.JointDrill2ApplyResp` | `6303` `proto.Error` |
| 6304 | `joint_drill_2_continue_req` | `proto.JointDrill2ContinueReq` | `6305` `proto.Nil` | `6306` `proto.Error` |
| 6307 | `joint_drill_2_sync_req` | `proto.JointDrill2SyncReq` | `6308` `proto.Nil` | `6309` `proto.Error` |
| 6310 | `joint_drill_2_give_up_req` | `proto.JointDrill2GiveUpReq` | `6311` `proto.JointDrillSettle` | `6312` `proto.Error` |
| 6313 | `joint_drill_2_retreat_req` | `proto.Nil` | `6314` `proto.Nil` | `6315` `proto.Error` |
| 6316 | `joint_drill_2_settle_req` | `proto.JointDrill2SettleReq` | `6317` `proto.JointDrillSettle` | `6318` `proto.Error` |
| 6319 | `joint_drill_2_game_over_req` | `proto.Nil` | `6320` `proto.JointDrillSettle` | `6321` `proto.Error` |
| 7013 | `plot_reward_receive_req` | `proto.UI32` | `7014` `proto.ChangeInfo` | `7015` `proto.Error` |
| 7016 | `daily_instance_apply_req` | `proto.DailyInstanceApplyReq` | `7017` `proto.Nil` | `7018` `proto.Error` |
| 7019 | `daily_instance_settle_req` | `proto.DailyInstanceSettleReq` | `7020` `proto.DailyInstanceSettleResp` | `7021` `proto.Error` |
| 7022 | `daily_instance_raid_req` | `proto.DailyInstanceRaidReq` | `7023` `proto.DailyInstanceRaidResp` | `7024` `proto.Error` |
| 7028 | `char_gem_instance_apply_req` | `proto.CharGemInstanceApplyReq` | `7029` `proto.ChangeInfo` | `7030` `proto.Error` |
| 7031 | `char_gem_instance_settle_req` | `proto.CharGemInstanceSettleReq` | `7032` `proto.CharGemInstanceSettleResp` | `7033` `proto.Error` |
| 7034 | `char_gem_instance_sweep_req` | `proto.CharGemInstanceSweepReq` | `7035` `proto.CharGemInstanceSweepResp` | `7036` `proto.Error` |
| 7101 | `region_boss_level_apply_req` | `proto.RegionBossLevelApplyReq` | `7102` `proto.ChangeInfo` | `7103` `proto.Error` |
| 7104 | `region_boss_level_settle_req` | `proto.RegionBossLevelSettleReq` | `7105` `proto.RegionBossLevelSettleResp` | `7106` `proto.Error` |
| 7107 | `region_boss_level_sweep_req` | `proto.RegionBossLevelSweepReq` | `7108` `proto.RegionBossLevelSweepResp` | `7109` `proto.Error` |
| 7201 | `traveler_duel_level_apply_req` | `proto.TravelerDuelLevelApplyReq` | `7202` `proto.Nil` | `7203` `proto.Error` |
| 7204 | `traveler_duel_level_settle_req` | `proto.TravelerDuelLevelSettleReq` | `7205` `proto.TravelerDuelLevelSettleResp` | `7206` `proto.Error` |
| 7207 | `traveler_duel_info_req` | `proto.Nil` | `7208` `proto.TravelerDuelInfo` | `7209` `proto.Error` |
| 7210 | `traveler_duel_quest_reward_receive_req` | `proto.TravelerDuelQuestRewardReceiveReq` | `7211` `proto.TravelerDuelQuestRewardReceiveResp` | `7212` `proto.Error` |
| 7213 | `traveler_duel_rank_req` | `proto.Nil` | `7214` `proto.TravelerDuelRankInfo` | `7215` `proto.Error` |
| 7216 | `traveler_duel_rank_upload_req` | `proto.TravelerDuelBattleSamples` | `7217` `proto.RankChange` | `7218` `proto.Error` |
| 7301 | `story_apply_req` | `proto.StoryApplyReq` | `7302` `proto.Nil` | `7303` `proto.Error` |
| 7304 | `story_settle_req` | `proto.StorySettleReq` | `7305` `proto.ChangeInfo` | `7306` `proto.Error` |
| 7401 | `skill_instance_apply_req` | `proto.SkillInstanceApplyReq` | `7402` `proto.ChangeInfo` | `7403` `proto.Error` |
| 7404 | `skill_instance_settle_req` | `proto.SkillInstanceSettleReq` | `7405` `proto.SkillInstanceSettleResp` | `7406` `proto.Error` |
| 7407 | `skill_instance_sweep_req` | `proto.SkillInstanceSweepReq` | `7408` `proto.SkillInstanceSweepResp` | `7409` `proto.Error` |
| 7410 | `week_boss_apply_req` | `proto.WeekBossApplyReq` | `7411` `proto.Nil` | `7412` `proto.Error` |
| 7413 | `week_boss_settle_req` | `proto.WeekBossSettleReq` | `7414` `proto.WeekBossLevelSettleResp` | `7415` `proto.Error` |
| 7501 | `tutorial_level_settle_req` | `proto.UI32` | `7502` `proto.Nil` | `7503` `proto.Error` |
| 7504 | `tutorial_level_reward_receive_req` | `proto.UI32` | `7505` `proto.ChangeInfo` | `7506` `proto.Error` |
| 7601 | `story_set_info_req` | `proto.Nil` | `7602` `proto.StorySetInfoResp` | `7603` `proto.Error` |
| 7604 | `story_set_reward_receive_req` | `proto.StorySetRewardReceiveReq` | `7605` `proto.ChangeInfo` | `7606` `proto.Error` |
| 8001 | `energy_buy_req` | `proto.UI32` | `8002` `proto.EnergyBuyResp` | `8003` `proto.Error` |
| 8004 | `energy_extract_req` | `proto.UI32` | `8005` `proto.ChangeInfo` | `8006` `proto.Error` |
| 8007 | `energy_info_req` | `proto.Nil` | `8008` `proto.Energy` | `8009` `proto.Error` |
| 8101 | `client_event_report_req` | `proto.Events` | `8102` `proto.Nil` | `8103` `proto.Error` |
| 8201 | `vampire_survivor_apply_req` | `proto.VampireSurvivorApplyReq` | `8202` `proto.VampireSurvivorApplyResp` | `8203` `proto.Error` |
| 8204 | `vampire_survivor_area_change_req` | `proto.VampireSurvivorAreaChangeReq` | `8205` `proto.Nil` | `8206` `proto.Error` |
| 8207 | `vampire_survivor_settle_req` | `proto.VampireSurvivorSettleReq` | `8208` `proto.VampireSurvivorSettleResp` | `8209` `proto.Error` |
| 8210 | `vampire_survivor_reward_select_req` | `proto.VampireSurvivorRewardSelectReq` | `8211` `proto.VampireSurvivorRewardSelectResp` | `8212` `proto.Error` |
| 8213 | `vampire_survivor_reward_chest_req` | `proto.VampireSurvivorRewardChestReq` | `8214` `proto.VampireSurvivorRewardChestResp` | `8215` `proto.Error` |
| 8216 | `vampire_survivor_quest_reward_receive_req` | `proto.VampireSurvivorQuestRewardReceiveReq` | `8217` `proto.ChangeInfo` | `8218` `proto.Error` |
| 8222 | `vampire_survivor_extra_reward_select_req` | `proto.VampireSurvivorRewardSelectReq` | `8223` `proto.VampireSurvivorRewardSelectResp` | `8224` `proto.Error` |
| 8225 | `vampire_survivor_restart_req` | `proto.Nil` | `8226` `proto.Nil` | `8227` `proto.Error` |
| 8301 | `vampire_talent_detail_req` | `proto.Nil` | `8302` `proto.VampireTalentDetailResp` | `8303` `proto.Error` |
| 8304 | `vampire_talent_reset_req` | `proto.Nil` | `8305` `proto.Nil` | `8306` `proto.Error` |
| 8307 | `vampire_talent_unlock_req` | `proto.UI32` | `8308` `proto.Nil` | `8309` `proto.Error` |
| 8310 | `vampire_talent_show_req` | `proto.Nil` | `8311` `proto.Nil` | `8312` `proto.Error` |
| 8313 | `vampire_survivor_info_req` | `proto.Nil` | `8314` `proto.VampireSurvivorInfoResp` | `8315` `proto.Error` |
| 8316 | `activity_story_apply_req` | `proto.ActivityStoryApplyReq` | `8317` `proto.Nil` | `8318` `proto.Error` |
| 8319 | `activity_story_settle_req` | `proto.ActivityStorySettleReq` | `8320` `proto.ChangeInfo` | `8321` `proto.Error` |
| 8322 | `activity_throw_gift_settle_req` | `proto.ActivityThrowGiftSettleReq` | `8323` `proto.ChangeInfo` | `8324` `proto.Error` |
| 8325 | `player_last_read_update_req` | `proto.LastRead` | `8326` `proto.Nil` | `8327` `proto.Error` |
| 8328 | `activity_history_story_apply_req` | `proto.ActivityHistoryStoryApplyReq` | `8329` `proto.Nil` | `8330` `proto.Error` |
| 8331 | `activity_history_story_settle_req` | `proto.ActivityHistoryStorySettleReq` | `8332` `proto.ChangeInfo` | `8333` `proto.Error` |
| 8401 | `activity_penguin_card_level_settle_req` | `proto.ActivityPenguinCardSettleReq` | `8402` `proto.ChangeInfo` | `8403` `proto.Error` |
| 8404 | `activity_penguin_card_quest_reward_receive_req` | `proto.ActivityPenguinCardQuestReceiveReq` | `8405` `proto.ChangeInfo` | `8406` `proto.Error` |
| 8407 | `activity_double_quest_reward_receive_req` | `proto.ActivityDoubleQuestRewardReq` | `8408` `proto.ChangeInfo` | `8409` `proto.Error` |
| 8411 | `activity_ice_cream_level_settle_req` | `proto.ActivityIceCreamLevelSettleReq` | `8412` `proto.ActivityIceCreamLevelSettleSucceed` | `8413` `proto.Error` |
| 8416 | `activity_penguin_card_endless_level_save_req` | `proto.ActivityPenguinCardEndlessLevelSaveReq` | `8417` `proto.Nil` | `8418` `proto.Error` |
| 8422 | `guide_mission_quest_reward_receive_req` | `proto.GuideMissionQuestRewardReceiveReq` | `8423` `proto.ChangeInfo` | `8424` `proto.Error` |
| 8430 | `activity_math_parkour_level_settle_req` | `proto.ActivityMathParkourLevelSettleReq` | `8431` `proto.ActivityMathParkourLevelSettleSucceed` | `8432` `proto.Error` |
| 8501 | `activity_returnee_info_req` | `proto.Nil` | `8502` `proto.ActivityReturneeInfoResp` | `8503` `proto.Error` |
| 8504 | `activity_returnee_reunion_reward_req` | `proto.Nil` | `8505` `proto.ChangeInfo` | `8506` `proto.Error` |
| 8507 | `activity_returnee_quest_point_reward_receive_req` | `proto.UI32` | `8508` `proto.ChangeInfo` | `8509` `proto.Error` |
| 8510 | `activity_returnee_avg_req` | `proto.Nil` | `8511` `proto.Nil` | `8512` `proto.Error` |
| 8513 | `activity_returnee_quest_reward_receive_req` | `proto.UI32` | `8514` `proto.ChangeInfo` | `8515` `proto.Error` |
| 8516 | `activity_returnee_login_reward_receive_req` | `proto.Nil` | `8517` `proto.ChangeInfo` | `8518` `proto.Error` |
| 8519 | `activity_returnee_card_reward_receive_req` | `proto.Nil` | `8520` `proto.ChangeInfo` | `8521` `proto.Error` |
| 8522 | `activity_returnee_card_order_req` | `proto.String` | `8523` `proto.OrderInfo` | `8524` `proto.Error` |
| 8525 | `activity_returnee_pack_order_req` | `proto.String` | `8526` `proto.OrderInfo` | `8527` `proto.Error` |
| 8601 | `activity_archive_story_apply_req` | `proto.ActivityArchiveStoryApplyReq` | `8602` `proto.Nil` | `8603` `proto.Error` |
| 8604 | `activity_archive_story_settle_req` | `proto.ActivityArchiveStorySettleReq` | `8605` `proto.ChangeInfo` | `8606` `proto.Error` |
| 8607 | `activity_archive_story_info_req` | `proto.UI32` | `8608` `proto.ActivityHistoryStoryChapter` | `8609` `proto.Error` |
| 8610 | `activity_mail_bind_reward_receive_req` | `proto.UI32` | `8611` `proto.ChangeInfo` | `8612` `proto.Error` |
| 8613 | `activity_archive_story_chapter_pass_req` | `proto.ActivityArchiveStoryChapterPassReq` | `8614` `proto.Nil` | `8615` `proto.Error` |
| 9104 | `activity_periodic_reward_receive_req` | `proto.ActivityPeriodicRewardReq` | `9105` `proto.ChangeInfo` | `9106` `proto.Error` |
| 9107 | `activity_periodic_final_reward_receive_req` | `proto.UI32` | `9108` `proto.ChangeInfo` | `9109` `proto.Error` |
| 9110 | `activity_login_reward_receive_req` | `proto.UI32` | `9111` `proto.ChangeInfo` | `9112` `proto.Error` |
| 9113 | `activity_tower_defense_story_reward_receive_req` | `proto.UI32` | `9114` `proto.ChangeInfo` | `9115` `proto.Error` |
| 9116 | `activity_tower_defense_quest_reward_receive_req` | `proto.ActivityTowerDefenseQuestReceiveReq` | `9117` `proto.ChangeInfo` | `9118` `proto.Error` |
| 9119 | `activity_tower_defense_level_apply_req` | `proto.ActivityTowerDefenseLevelApplyReq` | `9120` `proto.Nil` | `9121` `proto.Error` |
| 9122 | `activity_tower_defense_level_settle_req` | `proto.ActivityTowerDefenseLevelSettleReq` | `9123` `proto.ChangeInfo` | `9124` `proto.Error` |
| 9125 | `activity_trekker_versus_apply_req` | `proto.ActivityTrekkerVersusApplyReq` | `9126` `proto.Nil` | `9127` `proto.Error` |
| 9128 | `activity_trekker_versus_settle_req` | `proto.ActivityTrekkerVersusSettleReq` | `9129` `proto.ActivityTrekkerVersusSettleResp` | `9130` `proto.Error` |
| 9131 | `activity_trekker_versus_reward_receive_req` | `proto.UI32` | `9132` `proto.ActivityTrekkerVersusRewardReceiveResp` | `9133` `proto.Error` |
| 9134 | `activity_trekker_versus_idle_refresh_req` | `proto.UI32` | `9135` `proto.TrekkerVersusShow` | `9136` `proto.Error` |
| 9137 | `activity_trekker_versus_idle_reward_receive_req` | `proto.UI32` | `9138` `proto.ActivityTrekkerVersusIdleRewardReceiveResp` | `9139` `proto.Error` |
| 9140 | `activity_trekker_versus_rank_boost_req` | `proto.ActivityTrekkerVersusRankBoostReq` | `9141` `proto.ActivityTrekkerVersusRankBoostResp` | `9142` `proto.Error` |
| 9143 | `activity_trekker_versus_schedule_reward_receive_req` | `proto.ActivityTrekkerVersusScheduleRewardReceiveReq` | `9144` `proto.ActivityTrekkerVersusScheduleRewardReceiveResp` | `9145` `proto.Error` |
| 9146 | `activity_malllogin_reward_receive_req` | `proto.UI32` | `9147` `proto.ChangeInfo` | `9148` `proto.Error` |
| 9204 | `phone_contacts_report_req` | `proto.PhoneContactsReportReq` | `9205` `proto.ChangeInfo` | `9206` `proto.Error` |
| 9207 | `phone_contacts_top_req` | `proto.UI32` | `9208` `proto.Nil` | `9209` `proto.Error` |
| 9210 | `activity_share_reward_receive_req` | `proto.ActivityShareRewardReq` | `9211` `proto.ChangeInfo` | `9212` `proto.Error` |
| 9301 | `talent_unlock_req` | `proto.UI32` | `9302` `proto.TalentUnlockResp` | `9303` `proto.Error` |
| 9304 | `talent_reset_req` | `proto.TalentResetReq` | `9305` `proto.ChangeInfo` | `9306` `proto.Error` |
| 9307 | `talent_node_reset_req` | `proto.UI32` | `9308` `proto.TalentNodeResetResp` | `9309` `proto.Error` |
| 9310 | `talent_background_set_req` | `proto.TalentBackgroundSetReq` | `9311` `proto.Nil` | `9312` `proto.Error` |
| 9313 | `talent_group_unlock_req` | `proto.UI32` | `9314` `proto.TalentGroupUnlockResp` | `9315` `proto.Error` |
| 9401 | `activity_trial_reward_receive_req` | `proto.ActivityTrialRewardReceiveReq` | `9402` `proto.ChangeInfo` | `9403` `proto.Error` |
| 9501 | `activity_cg_read_req` | `proto.UI32` | `9502` `proto.ChangeInfo` | `9503` `proto.Error` |
| 9510 | `dorm_info_list_req` | `proto.Nil` | `9511` `proto.DormInfoListResp` | `9512` `proto.Error` |
| 9513 | `dorm_furniture_preset_list_req` | `proto.Nil` | `9514` `proto.DormFurniturePresetListResp` | `9515` `proto.Error` |
| 9516 | `dorm_bag_list_req` | `proto.Nil` | `9517` `proto.DormBagListResp` | `9518` `proto.Error` |
| 9519 | `dorm_furniture_preset_delete_req` | `proto.UI32` | `9520` `proto.Nil` | `9521` `proto.Error` |
| 9522 | `dorm_furniture_save_req` | `proto.DormFurnitureSaveReq` | `9523` `proto.I64` | `9524` `proto.Error` |
| 9525 | `dorm_info_update_name_req` | `proto.StringV` | `9526` `proto.Nil` | `9527` `proto.Error` |
| 9528 | `dorm_invitation_role_req` | `proto.DormInvitationRoleReq` | `9529` `proto.Nil` | `9530` `proto.Error` |
| 9531 | `dorm_reward_receive_req` | `proto.DormRewardReceiveReq` | `9532` `proto.ChangeInfo` | `9533` `proto.Error` |
| 9534 | `dorm_role_interaction_req` | `proto.DormRoleInteractionReq` | `9535` `proto.DormRoleInteractionResp` | `9536` `proto.Error` |
| 9537 | `dorm_affinity_gift_send_req` | `proto.DormAffinityGiftSendReq` | `9538` `proto.DormAffinityGiftSendResp` | `9539` `proto.Error` |
| 9540 | `dorm_friend_list_req` | `proto.Nil` | `9541` `proto.DormFriendListResp` | `9542` `proto.Error` |
| 9543 | `dorm_friend_visit_req` | `proto.DormFriendVisitReq` | `9544` `proto.DormFriendVisitResp` | `9545` `proto.Error` |
| 9547 | `dorm_layout_reward_receive_req` | `proto.DormLayoutRewardReceiveReq` | `9548` `proto.ChangeInfo` | `9549` `proto.Error` |
| 9550 | `dorm_room_bgm_req` | `proto.DormRoomBGMReq` | `9551` `proto.Nil` | `9552` `proto.Error` |
| 9553 | `dorm_friend_like_req` | `proto.DormFriendLikeReq` | `9554` `proto.Nil` | `9555` `proto.Error` |
| 9556 | `dorm_update_preset_name_req` | `proto.StringV` | `9557` `proto.Nil` | `9558` `proto.Error` |
| 9559 | `dorm_set_room_scene_req` | `proto.UI32V` | `9560` `proto.Nil` | `9561` `proto.Error` |
| 9562 | `dorm_achievement_reward_receive_req` | `proto.DormAchievementRewardReceiveReq` | `9563` `proto.AchievementComplete` | `9564` `proto.Error` |
| 9565 | `dorm_blocks_convert_req` | `proto.UI32` | `9566` `proto.ChangeInfo` | `9567` `proto.Error` |
| 9568 | `dorm_get_change_data_req` | `proto.Nil` | `9569` `proto.DormChangeDataResp` | `9570` `proto.Error` |
| 9571 | `dorm_learn_req` | `proto.NewbieInfo` | `9572` `proto.Nil` | `9573` `proto.Error` |
| 9574 | `dorm_get_furniture_num_req` | `proto.UI32` | `9575` `proto.ItemTpl` | `9576` `proto.Error` |
| 9601 | `activity_levels_apply_req` | `proto.ActivityLevelsApplyReq` | `9602` `proto.ChangeInfo` | `9603` `proto.Error` |
| 9604 | `activity_levels_settle_req` | `proto.ActivityLevelsSettleReq` | `9605` `proto.ActivityLevelsSettleResp` | `9606` `proto.Error` |
| 9607 | `activity_levels_sweep_req` | `proto.ActivityLevelsSweepReq` | `9608` `proto.ActivityLevelsSweepResp` | `9609` `proto.Error` |
| 9701 | `activity_avg_reward_receive_req` | `proto.ActivityAvgRewardReceiveReq` | `9702` `proto.ChangeInfo` | `9703` `proto.Error` |
| 9751 | `activity_shop_purchase_req` | `proto.ActivityShopPurchaseReq` | `9752` `proto.ActivityShopPurchaseResp` | `9753` `proto.Error` |
| 9801 | `battle_pass_info_req` | `proto.Nil` | `9802` `proto.BattlePassInfo` | `9803` `proto.Error` |
| 9804 | `battle_pass_reward_receive_req` | `proto.BattlePassRewardReceiveReq` | `9805` `proto.BattlePassRewardReceiveResp` | `9806` `proto.Error` |
| 9807 | `battle_pass_level_buy_req` | `proto.UI32V` | `9808` `proto.BattlePassLevelBuyResp` | `9809` `proto.Error` |
| 9810 | `battle_pass_order_req` | `proto.BattlePassOrderReq` | `9811` `proto.OrderInfo` | `9812` `proto.Error` |
| 9813 | `battle_pass_order_collect_req` | `proto.String` | `9814` `proto.BattlePassOrderCollectResp` | `9815` `proto.CollectResp` |
| 9816 | `battle_pass_quest_reward_receive_req` | `proto.UI32` | `9817` `proto.BattlePassQuestRewardResp` | `9818` `proto.Error` |
| 9901 | `redeem_code_req` | `proto.String` | `9902` `proto.RedeemCodeResp` | `9903` `proto.Error` |
| 11001 | `activity_mining_apply_req` | `proto.ActivityMiningApplyReq` | `11002` `proto.ActivityMiningApplyResp` | `11003` `proto.Error` |
| 11004 | `activity_mining_dig_req` | `proto.ActivityMiningDigReq` | `11005` `proto.ActivityMiningDigResp` | `11006` `proto.Error` |
| 11007 | `activity_mining_move_to_next_layer_req` | `proto.ActivityMiningMoveToNextLayerReq` | `11008` `proto.ActivityMiningMoveToNextLayerResp` | `11009` `proto.Error` |
| 11010 | `activity_mining_story_reward_receive_req` | `proto.ActivityMiningStoryRewardReceiveReq` | `11011` `proto.ChangeInfo` | `11012` `proto.Error` |
| 11013 | `activity_mining_quest_reward_receive_req` | `proto.ActivityMiningQuestRewardReceiveReq` | `11014` `proto.ChangeInfo` | `11015` `proto.Error` |
| 11101 | `score_boss_apply_req` | `proto.ScoreBossApplyReq` | `11102` `proto.Nil` | `11103` `proto.Error` |
| 11104 | `score_boss_settle_req` | `proto.ScoreBossSettleReq` | `11105` `proto.ScoreBossSettleResp` | `11106` `proto.Error` |
| 11107 | `score_boss_rank_req` | `proto.Nil` | `11108` `proto.ScoreBossRankInfo` | `11109` `proto.Error` |
| 11110 | `score_boss_star_reward_receive_req` | `proto.ScoreBossStarRewardReceiveReq` | `11111` `proto.ChangeInfo` | `11112` `proto.Error` |
| 11201 | `activity_cookie_settle_req` | `proto.ActivityCookieSettleReq` | `11202` `proto.ChangeInfo` | `11203` `proto.Error` |
| 11204 | `activity_cookie_quest_reward_receive_req` | `proto.ActivityCookieQuestRewardReceiveReq` | `11205` `proto.ChangeInfo` | `11206` `proto.Error` |

## 其余未接入的响应和通知

包括已接入请求缺少的响应、独立同步响应和服务端通知。

| 消息号 | 名称 | Protobuf 类型 |
| ---: | --- | --- |
| -10059 | `handbook_complete_notify` | `proto.GmHandbookCompleteNotify` |
| -10058 | `clear_all_activity_mathParkour_levels_notify` | `proto.GmClearAllActivityMathParkourLevelsNotify` |
| -10057 | `clear_all_activity_iceCream_levels_notify` | `proto.GmClearAllActivityIceCreamLevelsNotify` |
| -10056 | `sd_soldier_effect_notify` | `proto.SoldierEffectNtf` |
| -10055 | `sd_soldier_info_notify` | `proto.SoldierInfo` |
| -10054 | `sd_shop_data_notify` | `proto.ShopCaseData` |
| -10053 | `sd_item_change_notify` | `proto.ChangeInfo` |
| -10052 | `sd_buff_notify` | `proto.SoldierBuff` |
| -10051 | `clear_all_activity_golden_spy_levels_notify` | `proto.ActivityGDS` |
| -10050 | `clear_all_activity_breakout_levels_notify` | `proto.Milkout` |
| -10049 | `clear_story_set_notify` | `proto.StorySetInfoResp` |
| -10048 | `unlock_activity_story_notify` | `proto.ActivityHistoryStoryChapter` |
| -10047 | `assist_add_build_notify` | `proto.AssistAddBuildNotify` |
| -10046 | `clear_all_activity_levels_notify` | `proto.GmClearAllActivityLevelsNotify` |
| -10045 | `clear_all_activity_avg_notify` | `proto.GmClearAllActivityAvgNotify` |
| -10044 | `vs_add_fate_card_notify` | `proto.CardInfo` |
| -10043 | `char_gems_import_notify` | `proto.GmImportCharGemsNotify` |
| -10042 | `char_gems_export_notify` | `proto.String` |
| -10040 | `clear_all_char_gem_instance_notify` | `proto.GmClearAllCharGemInstance` |
| -10039 | `change_npc_affinity_notify` | `proto.NPCAffinityInfo` |
| -10038 | `tower_change_sub_note_skill_notify` | `proto.TowerChangeData` |
| -10037 | `world_class_quest_complete_notify` | `proto.Quests` |
| -10036 | `score_boss_level_reset_notify` | `proto.ScoreBossInfo` |
| -10035 | `score_boss_star_reward_reset_notify` | `proto.Nil` |
| -10034 | `char_up_change_notify` | `proto.GmCharUpNotify` |
| -10033 | `tower_growth_node_change_notify` | `proto.GmTowerGrowthNodesNotify` |
| -10032 | `st_harmony_skill_notify` | `proto.GmHarmonySkillNotify` |
| -10031 | `clear_all_story_notify` | `proto.GmStory` |
| -10030 | `story_complete_notify` | `proto.GmStory` |
| -10029 | `infinity_tower_skip_floor_notify` | `proto.InfinityTowerLevelInfo` |
| -10028 | `infinity_tower_all_infos_notify` | `proto.GmAllInfinityTowerInfos` |
| -10027 | `clear_all_equipment_instance_notify` | `proto.GmClearAllEquipmentInstance` |
| -10026 | `st_import_build_notify` | `proto.StarTowerBuildInfo` |
| -10025 | `st_export_build_notify` | `proto.String` |
| -10024 | `st_add_new_case_notify` | `proto.StarTowerRoomCase` |
| -10023 | `st_add_team_exp_notify` | `proto.STAddTeamExpNtf` |
| -10022 | `st_skip_floor_notify` | `proto.StarTowerInfo` |
| -10021 | `st_clear_all_star_tower_notify` | `proto.GmSTClearAllStarTower` |
| -10020 | `st_items_change_notify` | `proto.ChangeInfo` |
| -10019 | `char_affinity_final_notify` | `proto.CharAffinity` |
| -10018 | `clear_all_skill_instance_notify` | `proto.GmClearAllSkillInstance` |
| -10017 | `disc_reset_notify` | `proto.Disc` |
| -10016 | `clear_all_traveler_due_notify` | `proto.GmClearAllTravelerDue` |
| -10015 | `clear_all_region_boss_level_notify` | `proto.GmClearAllRegionBossLevel` |
| -10014 | `clear_all_week_boss_notify` | `proto.WeekBossLevels` |
| -10013 | `clear_all_daily_instance_notify` | `proto.GmClearAllDailyInstance` |
| -10012 | `refresh_agent_notify` | `proto.AgentInfos` |
| -10011 | `star_tower_sub_note_skill_info_notify` | `proto.StarTowerSubNoteSkillInfo` |
| -10010 | `vampire_survivor_talent_node_notify` | `proto.VampireTalentDetailResp` |
| -10009 | `add_vampire_season_score_notify` | `proto.UI32` |
| -10008 | `clear_all_vampire_survivor_notify` | `proto.GmClearAllVampireSurvivor` |
| -10007 | `region_boss_level_final_notify` | `proto.RegionBossLevel` |
| -10006 | `chars_final_notify` | `proto.Chars` |
| -10005 | `world_class_number_notify` | `proto.GmWorldClass` |
| -10004 | `char_change_notify` | `proto.Char` |
| -10003 | `char_reset_notify` | `proto.Char` |
| -10001 | `items_change_notify` | `proto.ChangeInfo` |
| 1003 | `player_data_failed_ack` | `proto.Error` |
| 1014 | `player_ping_failed_ack` | `proto.Error` |
| 4724 | `potential_preselection_list_failed_ack` | `proto.Error` |
| 5121 | `mall_package_list_failed_ack` | `proto.Error` |
| 5321 | `soldier_season_info_failed_ack` | `proto.Error` |
| 5403 | `soldier_technology_info_failed_ack` | `proto.Error` |
| 6006 | `gacha_information_failed_ack` | `proto.Error` |
| 6024 | `gacha_newbie_info_failed_ack` | `proto.Error` |
| 6025 | `gacha_spin_sync_ack` | `proto.GachaSpinSyncResp` |
| 8421 | `guide_mission_info_failed_ack` | `proto.Error` |
| 9103 | `activity_detail_failed_ack` | `proto.Error` |
| 9203 | `phone_contacts_info_failed_ack` | `proto.Error` |
| 10000 | `system_failed_ack` | `proto.Error` |
| 10002 | `mail_state_notify` | `proto.MailState` |
| 10003 | `player_relogin_notify` | `proto.Error` |
| 10004 | `token_expire_notify` | `proto.Error` |
| 10005 | `player_ban_notify` | `proto.Error` |
| 10006 | `quest_change_notify` | `proto.Quest` |
| 10007 | `week_boss_refresh_ticket_notify` | `proto.ChangeInfo` |
| 10008 | `agent_new_notify` | `proto.NewAgent` |
| 10009 | `world_class_change_notify` | `proto.WorldClassUpdate` |
| 10010 | `friend_energy_state_notify` | `proto.FriendEnergyState` |
| 10011 | `signin_reward_change_notify` | `proto.SigninRewardUpdate` |
| 10012 | `friend_state_notify` | `proto.FriendState` |
| 10013 | `order_paid_notify` | `proto.OrderStateChange` |
| 10014 | `order_revoke_notify` | `proto.ChangeInfo` |
| 10015 | `star_tower_book_potential_notify` | `proto.StarTowerBookPotentialChange` |
| 10016 | `star_tower_book_event_notify` | `proto.StarTowerBookEventChange` |
| 10017 | `battle_pass_state_notify` | `proto.BattlePassState` |
| 10018 | `world_class_reward_state_notify` | `proto.WorldClassRewardState` |
| 10019 | `char_advance_reward_state_notify` | `proto.CharAdvanceRewardState` |
| 10020 | `achievement_change_notify` | `proto.Achievement` |
| 10021 | `achievement_state_notify` | `proto.AchievementState` |
| 10022 | `character_skin_gain_notify` | `proto.Skin` |
| 10023 | `character_skin_change_notify` | `proto.SkinChange` |
| 10024 | `handbook_change_notify` | `proto.HandbookInfo` |
| 10025 | `monthly_card_rewards_notify` | `proto.MonthlyCardRewards` |
| 10026 | `quest_state_notify` | `proto.QuestState` |
| 10027 | `mall_package_state_notify` | `proto.MallPackageState` |
| 10028 | `dictionary_change_notify` | `proto.Dictionary` |
| 10029 | `activity_change_notify` | `proto.Activity` |
| 10030 | `activity_quest_change_notify` | `proto.ActivityQuest` |
| 10031 | `char_affinity_reward_state_notify` | `proto.CharAffinityRewardState` |
| 10032 | `mail_overflow_notify` | `proto.Nil` |
| 10033 | `infinity_tower_rewards_state_notify` | `proto.UI32` |
| 10034 | `phone_chat_change_notify` | `proto.UI32` |
| 10035 | `character_fragments_overflow_change_notify` | `proto.ChangeInfo` |
| 10036 | `activity_login_rewards_notify` | `proto.ActivityLogin` |
| 10037 | `tower_book_fate_card_collect_notify` | `proto.TowerBookFateCardCollectNotify` |
| 10038 | `tower_book_fate_card_reward_notify` | `proto.TowerBookFateCardRewardChangeNotify` |
| 10039 | `region_boss_level_challenge_ticket_notify` | `proto.ChangeInfo` |
| 10040 | `honor_change_notify` | `proto.HonorChangeNotify` |
| 10041 | `activity_mining_daily_reward_notify` | `proto.ChangeInfo` |
| 10042 | `activity_mining_supplement_reward_notify` | `proto.ChangeInfo` |
| 10043 | `activity_mining_energy_convert_notify` | `proto.ChangeInfo` |
| 10044 | `notice_change_notify` | `proto.NoticeChangeNotify` |
| 10045 | `activity_state_change_notify` | `proto.ActivityState` |
| 10046 | `activity_joint_drill_refresh_ticket_notify` | `proto.ChangeInfo` |
| 10047 | `force_update_notify` | `proto.UI64` |
| 10048 | `player_head_icon_change_notify` | `proto.PlayerHeadIconChangeNotify` |
| 10049 | `activity_mining_enter_layer_notify` | `proto.ActivityMiningEnterLayerNotify` |
| 10050 | `story_set_state_notify` | `proto.UI32` |
| 10051 | `vampire_survivor_new_season_notify` | `proto.UI32` |
| 10052 | `order_collected_notify` | `proto.OrderCollected` |
| 10053 | `activity_end_item_change_notify` | `proto.ChangeInfo` |
| 10054 | `item_expired_change_notify` | `proto.ItemTpl` |
| 10055 | `milkout_character_unlock_notify` | `proto.MilkoutCharacterUnlockNotify` |
| 10056 | `activity_double_reward_times_notify` | `proto.ActivityDoubleRewardTimesNotify` |
| 10057 | `trace_hunt_new_control_notify` | `proto.Nil` |
| 10058 | `trace_hunt_state_notify` | `proto.TraceHuntState` |
| 10060 | `trace_hunt_item_change_notify` | `proto.TraceHuntItem` |
| 10061 | `score_boss_reset_score_notify` | `proto.Nil` |
| 10062 | `activity_returnee_state_notify` | `proto.ActivityReturneeState` |
| 10063 | `activity_returnee_quest_change_notify` | `proto.Quest` |
| 10064 | `dorm_room_unlock_notify` | `proto.DormRoomUnlock` |
| 10065 | `guide_mission_state_notify` | `proto.GuideMissionState` |
| 10066 | `activity_malllogin_rewards_notify` | `proto.ActivityMallLogin` |
| 10067 | `achievement_complete_notify` | `proto.AchievementInfo` |
| 11115 | `score_boss_info_failed_ack` | `proto.Error` |
