# 未实现的功能与协议清单

统计口径：`reverse/output/protocol/protocols.json`（1171 个消息号）+ `Game/LocalGameGateway*.cs` 实际分支。数据更新于 2026-11-23。

公告 HTTP 链路已补齐：GM 可编辑并发布公告，网关读取 `game_notices` 生成有内容的加密分类列表与加密 HTML 正文，包含渠道及有效期过滤。该能力不计入游戏 Protobuf 消息号；`10044 notice_change_notify` 仍缺。GM 功能已按底部六类导航拆分，后台启动脚本使用独立运行目录避免占用 VS 编译文件。

| 维度 | 总数 | 已实现 | 未实现 |
| --- | ---: | ---: | ---: |
| 请求 | 349 | 42 | **307** |
| 通知（notify） | 123 | 1（`10001 player_new_notify`） | **122**（57 个 GM 指令类负数编号 + 65 个业务通知） |
| 成功应答 | 349 | 41 | 308（含 6025 抽卡同步） |
| 失败应答 | 350 | 28 | 322 |
| 合计 | 1171 | 112 | **1059** |

已实现失败号共 28 个，包含握手失败 `3 ike_failed_ack`、既有登录/建角/引导/展示失败，以及本批新增的资料、邮件及好友失败号。可运行 `python server/Stella.LocalGateway/verify_protocol_gaps.py` 重新核对统计及第三节请求表的遗漏、重复。

> 每个请求实现时应一并接入客户端注册的失败号。多数失败包为 `proto.Error`（业务错误码取自 `reverse/output/tables/ErrorCode.json`），但 `5112/9815` 的类型是 `proto.CollectResp`，不能统一编码成 Error。固定失败号以协议索引为准，不限于已接入请求所对应的编号。

本批同时修复重登：SDK 长期票据改为可重建游戏会话并持久化哈希，服务重启后仍有效；UID 不匹配、未知、过期或密码版本已变的票据继续拒绝。旧版内存票据无法迁移，更新后首次需重新输入账号密码。好友视图按 Config 新角色称号默认值 1/2 补齐缺失字段，避免客户端查询 Title[0] 报错。

## 一、功能级缺口（不是补单个协议能解决）

| # | 缺口 | 影响 |
| ---: | --- | --- |
| 1 | 建角后序章到大厅链路待实机核验 | `CreatePlayerCtrl` 无条件调用 `EnterTowerPrologue`，但该函数本地组装塔 999 数据并直接进入，不发送 4601。`StarTowerPrologueLevel:StarTowerClear` 在剧情结束后调用 `SetPlayerLearnReq(1,-1)`（1015，网关已实现）。组 1 预置 -1 只影响后续登录分支，不能跳过建角回调；卡点需实机日志定位。 |
| 2 | 1004 成功回包号未实机核验 | 客户端没有 `player_reg_succeed_ack`，本地服回 `1002 PlayerInfo`，属于代码层唯一自洽解，但没在手机上确认过。 |
| 3 | 引导任务面板写死 | 8419 固定返回「第 1 章完成」，缺少独立的章节、任务进度和领奖记录。客户端 `PlayerQuestData:RequestGuideMissionInfo` 会把 Done 章节的所有任务填为已领取；该系统不等同于 `Acc.Newbies` 的操作引导。 |
| 4 | 背包 / 货币 / 发奖链路覆盖不全 | 邮件 9007→9008 已打通资源和普通背包道具的 `ChangeInfo` 发奖与持久化；1101 道具使用、碎片转换及其他奖励入口仍缺失。 |
| 5 | 服务器通知几乎没发 | 123 个 `*_notify` 里只实现了 `10001`，其余 122 个全缺。异步状态变化缺少通知；写操作若成功回包携带 `ChangeInfo`，客户端仍能即时更新，不能仅凭缺通知判断必须重登。 |
| 6 | 断线与会话治理缺失 | `player_relogin_notify(10003)`、`token_expire_notify(10004)`、`player_ban_notify(10005)` 未实现；重复登录不踢旧会话。 |
| 7 | 无战斗/结算校验 | 所有 `*_settle`、`*_apply` 依赖真实战斗结果，本地服目前只能做假结算，无法验证数值。 |
| 8 | 无支付与订单体系 | 商城、月卡、战令、回归礼包的下单/回调/领取全部缺失，`order_paid_notify` 也没有。 |
| 9 | 游戏内 GM 指令通道未接入 | `sudo_req(-1)` 未实现；独立 GM 后台已支持在线改资料、资源、密码、发邮件和注册码管理。 |
| 10 | 宿舍社交与排行仍缺 | 基础好友关系、申请、星标、每日赠领体力已持久化；宿舍拜访与各类排行榜（`*_rank_req`）仍未接入。 |

## 二、已接入但只有空壳（返回初始状态，业务逻辑未实现）

| 请求 | 回包 | 现状 | 缺什么 |
| ---: | --- | --- | --- |
| `4722` | 4723 空列表 | 潜能预选列表永远为空 | 预选方案存储与导入 |
| `5119` | 5120 空列表 | 礼包列表永远为空 | 礼包配置与状态 |
| `5319` | 5320 只回赛季号 | 士兵赛季零进度 | 赛季进度存储 |
| `5401` | 5402 空位图 | 科技节点全未解锁 | 科技解锁与消耗 |
| `6004 / 6022` | 6005 / 6023 | 卡池与新兵记录为空 | 卡池配置、抽卡与保底 |
| `8419` | 8420 硬编码 | 固定「第 1 章完成」 | 按 GuideMissionChapter/QuestGroup/Quest 配置和独立任务存档生成，不能从 `Acc.Newbies` 推导 |
| `9101` | 9102 空列表 | 活动列表永远为空 | 活动配置与时间窗 |
| `9201` | 9202 空通讯录 | 通讯录没有角色 | 角色解锁数据 |
| `11113` | 11114 固定赛季 13 | 积分 BOSS 信息写死 | 赛季与分数存储 |

## 三、完全未接入的请求（307 个，按功能模块）

“成功回包”列即客户端注册的成功消息号；“失败回包”以客户端注册类型为准，除 `5112/9815` 为 `proto.CollectResp` 外均为 `proto.Error`。

### GM / 管理指令（1）

游戏内 sudo 指令未接入；独立 GM 管理后台通过 SQLite 事务提供账号与存档管理。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| -1 | `sudo_req` | `proto.SudoCommand` | -2 `Nil` | -3 `Error` |

### 玩家资料与展示（9）

已接入改名、当前头像保存/列表、签名、展示成员和展示皮肤；本节剩余称号、性别、荣誉、音乐、退出登录、问卷、世界等级奖励等。头像目前仅返回当前已解锁头像，完整解锁台账仍未实现。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 1018 | `player_destroy_req` | `proto.Nil` | 1019 `PlayerDestroy` | 1020 `Error` |
| 1024 | `player_world_class_reward_receive_req` | `proto.PlayerWorldClassRewardReq` | 1025 `ChangeInfo` | 1026 `Error` |
| 1030 | `player_title_edit_req` | `proto.PlayerTitleEditReq` | 1031 `Nil` | 1032 `Error` |
| 1039 | `player_gender_edit_req` | `proto.Nil` | 1040 `Nil` | 1041 `Error` |
| 1042 | `player_survey_req` | `proto.PlayerSurveyReq` | 1043 `PlayerSurveyResp` | 1044 `Error` |
| 1045 | `player_exit_req` | `proto.Nil` | 1046 `Nil` | 1047 `Error` |
| 1048 | `player_honor_edit_req` | `proto.PlayerHonorEditReq` | 1049 `Nil` | 1050 `Error` |
| 1051 | `player_world_class_advance_req` | `proto.Nil` | 1052 `ChangeInfo` | 1053 `Error` |
| 1054 | `player_music_set_req` | `proto.I64` | 1055 `Nil` | 1056 `Error` |

### 道具与资源（7）

背包与货币变动整套缺失：道具使用、宝石兑换、道具合成、碎片转换、每日商店/商城奖励、快速成长。邮件领取已下发 `ChangeInfo`，本节道具操作仍未接入。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 1101 | `item_use_req` | `proto.ItemUseReq` | 1102 `ChangeInfo` | 1103 `Error` |
| 1104 | `gem_convert_req` | `proto.UI32` | 1105 `ChangeInfo` | 1106 `Error` |
| 1107 | `item_product_req` | `proto.ItemProductReq` | 1108 `ChangeInfo` | 1109 `Error` |
| 1110 | `fragments_convert_req` | `proto.Nil` | 1111 `ChangeInfo` | 1112 `Error` |
| 1113 | `daily_shop_reward_receive_req` | `proto.Nil` | 1114 `ChangeInfo` | 1115 `Error` |
| 1116 | `item_quick_growth_req` | `proto.ItemGrowthReq` | 1117 `ChangeInfo` | 1118 `Error` |
| 1119 | `daily_mall_reward_receive_req` | `proto.Nil` | 1120 `ChangeInfo` | 1121 `Error` |

好友 1201–1234 的 12 个请求本批均已接入，含完整成功/失败回包及持久化，已移出本节。好友上限 50、申请上限 30；每日 05:00（UTC+8）刷新赠送状态，每好友可赠 1 体力，日收取上限 30。异步好友通知与宿舍好友入口仍待补。

### 星塔成长（2）

1301 只返回空详情；节点解锁与组节点解锁未做，成长树无法推进。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 1304 | `tower_growth_node_unlock_req` | `proto.UI32` | 1305 `ChangeInfo` | 1306 `Error` |
| 1307 | `tower_growth_group_node_unlock_req` | `proto.UI32` | 1308 `TowerGrowthGroupNodeUnlockResp` | 1309 `Error` |

### 限时闯关与活动结算（3）

Milkout 结算、GDS 结算、士兵任务奖励。都属于「战斗结算 + 发奖」链路。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 1401 | `milkout_settle_req` | `proto.MilkoutSettleReq` | 1402 `ChangeInfo` | 1403 `Error` |
| 1501 | `activity_gds_settle_req` | `proto.ActivityGDSSettleReq` | 1502 `ChangeInfo` | 1503 `Error` |
| 1601 | `activity_soldier_quest_reward_receive_req` | `proto.ActivitySoldierQuestRewardReceiveReq` | 1602 `ChangeInfo` | 1603 `Error` |

### 编队与角色养成（25）

编队保存、角色升级/突破/技能/皮肤/招募、好感任务与礼物、约会与档案、宝石生成/刷新/替换/锁定/预设/装备/超锁。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 2001 | `player_formation_req` | `proto.PlayerFormationReq` | 2002 `Nil` | 2003 `Error` |
| 2301 | `char_upgrade_req` | `proto.CharUpgradeReq` | 2302 `CharUpgradeResp` | 2303 `Error` |
| 2304 | `char_advance_req` | `proto.UI32` | 2305 `ChangeInfo` | 2306 `Error` |
| 2307 | `char_skill_upgrade_req` | `proto.CharSkillUpgradeReq` | 2308 `ChangeInfo` | 2309 `Error` |
| 2313 | `char_advance_reward_receive_req` | `proto.CharAdvanceRewardReceiveReq` | 2314 `CharAdvanceRewardReceiveResp` | 2315 `Error` |
| 2316 | `char_skin_set_req` | `proto.CharSkinSetReq` | 2317 `Nil` | 2318 `Error` |
| 2322 | `char_affinity_quest_reward_receive_req` | `proto.CharAffinityQuestRewardReceiveReq` | 2323 `CharAffinityQuestRewardReceiveResp` | 2324 `Error` |
| 2325 | `char_recruitment_req` | `proto.UI32` | 2326 `ChangeInfo` | 2327 `Error` |
| 2328 | `char_affinity_gift_send_req` | `proto.CharAffinityGiftSendReq` | 2329 `CharAffinityGiftSendResp` | 2330 `Error` |
| 2401 | `char_dating_landmark_select_req` | `proto.CharDatingLandmarkSelectReq` | 2402 `CharDatingLandmarkSelectResp` | 2403 `Error` |
| 2404 | `char_dating_gift_send_req` | `proto.CharDatingGiftSendReq` | 2405 `CharDatingGiftSendResp` | 2406 `Error` |
| 2407 | `char_dating_event_reward_receive_req` | `proto.CharDatingEventRewardReceiveReq` | 2408 `ChangeInfo` | 2409 `Error` |
| 2410 | `char_archive_reward_receive_req` | `proto.CharArchiveRewardReceiveReq` | 2411 `ChangeInfo` | 2412 `Error` |
| 2413 | `char_dating_branch_a_select_req` | `proto.CharDatingBranchASelectReq` | 2414 `CharDatingBranchASelectResp` | 2415 `Error` |
| 2416 | `char_dating_branch_b_select_req` | `proto.CharDatingBranchBSelectReq` | 2417 `CharDatingBranchBSelectResp` | 2418 `Error` |
| 2501 | `char_gem_generate_req` | `proto.CharGemGenerateReq` | 2502 `CharGemGenerateResp` | 2503 `Error` |
| 2504 | `char_gem_refresh_req` | `proto.CharGemRefreshReq` | 2505 `CharGemRefreshResp` | 2506 `Error` |
| 2507 | `char_gem_replace_attribute_req` | `proto.CharGemReplaceAttributeReq` | 2508 `Nil` | 2509 `Error` |
| 2510 | `char_gem_update_gem_lock_status_req` | `proto.CharGemUpdateGemLockStatusReq` | 2511 `Nil` | 2512 `Error` |
| 2513 | `char_gem_use_preset_req` | `proto.CharGemUsePresetReq` | 2514 `Nil` | 2515 `Error` |
| 2516 | `char_gem_rename_preset_req` | `proto.CharGemRenamePresetReq` | 2517 `Nil` | 2518 `Error` |
| 2519 | `char_gem_equip_gem_req` | `proto.CharGemEquipGemReq` | 2520 `Nil` | 2521 `Error` |
| 2522 | `char_gem_overlock_revert_req` | `proto.CharGemOverlockRevertReq` | 2523 `ChangeInfo` | 2524 `Error` |
| 2525 | `char_gem_overlock_req` | `proto.CharGemOverlockReq` | 2526 `ChangeInfo` | 2527 `Error` |
| 2601 | `char_favorite_set_req` | `proto.UI32` | 2602 `Nil` | 2603 `Error` |

### 唱片与代理人（8）

唱片强化/升阶/限突/阅读奖励/一键限突，代理人申请/放弃/领奖。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 3119 | `disc_strengthen_req` | `proto.DiscStrengthenReq` | 3120 `DiscStrengthenResp` | 3121 `Error` |
| 3122 | `disc_promote_req` | `proto.DiscPromoteReq` | 3123 `DiscPromoteResp` | 3124 `Error` |
| 3125 | `disc_limit_break_req` | `proto.DiscLimitBreakReq` | 3126 `DiscLimitBreakResp` | 3127 `Error` |
| 3128 | `disc_read_reward_receive_req` | `proto.DiscReadRewardReceiveReq` | 3129 `ChangeInfo` | 3130 `Error` |
| 3131 | `disc_all_limit_break_req` | `proto.Nil` | 3132 `DiscAllLimitBreakResp` | 3133 `Error` |
| 3301 | `agent_apply_req` | `proto.AgentApplyReq` | 3302 `AgentApplyResp` | 3303 `Error` |
| 3304 | `agent_give_up_req` | `proto.AgentGiveUpReq` | 3305 `AgentGiveUpResp` | 3306 `Error` |
| 3307 | `agent_reward_receive_req` | `proto.AgentRewardReceiveReq` | 3308 `AgentRewardReceiveResp` | 3309 `Error` |

### 任务与图鉴（11）

引导任务、每日/周常/协助/宿舍任务奖励、图鉴奖励、星塔任务、活跃度宝箱、引导组奖励。任务系统没有存档结构。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 4201 | `quest_tour_guide_reward_receive_req` | `proto.UI32` | 4202 `TourGuideQuestRewardResp` | 4203 `Error` |
| 4204 | `quest_daily_reward_receive_req` | `proto.UI32` | 4205 `ChangeInfo` | 4206 `Error` |
| 4207 | `dictionary_reward_receive_req` | `proto.DictRewardReq` | 4208 `ChangeInfo` | 4209 `Error` |
| 4210 | `quest_tower_reward_receive_req` | `proto.UI32` | 4211 `ChangeInfo` | 4212 `Error` |
| 4213 | `quest_daily_active_reward_receive_req` | `proto.Nil` | 4214 `QuestDailyActiveRewardReceiveResp` | 4215 `Error` |
| 4216 | `quest_tour_guide_group_reward_receive_req` | `proto.Nil` | 4217 `TourGuideQuestGroupRewardResp` | 4218 `Error` |
| 4219 | `quest_weekly_reward_receive_req` | `proto.UI32` | 4220 `ChangeInfo` | 4221 `Error` |
| 4222 | `quest_weekly_active_reward_receive_req` | `proto.Nil` | 4223 `QuestWeeklyActiveRewardReceiveResp` | 4224 `Error` |
| 4225 | `quest_assist_reward_receive_req` | `proto.AssistQuestReceiveReq` | 4226 `ChangeInfo` | 4227 `Error` |
| 4228 | `quest_assist_group_reward_receive_req` | `proto.UI32` | 4229 `AssistGroupRewardResp` | 4230 `Error` |
| 4231 | `quest_dorm_reward_receive_req` | `proto.UI32V` | 4232 `QuestDormRewardReceiveSucceedAck` | 4233 `Error` |

### 成就与活动任务（4）

成就领奖与成就信息、活动任务及组奖励。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 4301 | `activity_task_reward_receive_req` | `proto.ActivityTaskRewardReceiveReq` | 4302 `ChangeInfo` | 4303 `Error` |
| 4304 | `activity_task_group_reward_receive_req` | `proto.UI32` | 4305 `ChangeInfo` | 4306 `Error` |
| 4401 | `achievement_reward_receive_req` | `proto.AchievementRewardReq` | 4402 `ChangeInfo` | 4403 `Error` |
| 4404 | `achievement_info_req` | `proto.Nil` | 4405 `Achievements` | 4406 `Error` |

### 无限塔（5）

信息、申请、结算、每日奖励、剧情奖励。与星塔共用房间/构筑概念，但没有存档。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 4501 | `infinity_tower_info_req` | `proto.Nil` | 4502 `InfinityTowerInfoResp` | 4503 `Error` |
| 4504 | `infinity_tower_apply_req` | `proto.InfinityTowerApplyReq` | 4505 `Nil` | 4506 `Error` |
| 4507 | `infinity_tower_settle_req` | `proto.InfinityTowerSettleReq` | 4508 `InfinityTowerSettleResp` | 4509 `Error` |
| 4510 | `infinity_tower_daily_reward_receive_req` | `proto.Nil` | 4511 `InfinityTowerDailyRewardReceiveResp` | 4512 `Error` |
| 4513 | `infinity_tower_plot_reward_receive_req` | `proto.UI32` | 4514 `InfinityTowerPlotRewardReceiveResp` | 4515 `Error` |

### 星塔与星塔构筑（25）

**4601 是普通星塔入场协议，序章并不发送它**：`PlayerStarTowerData:EnterTower` 发 4601；`EnterTowerPrologue` 本地组装塔 999 并直接调用初始化。普通星塔仍缺入场、交互、信息、放弃、构筑保存/列表/详情/删除/命名/锁定/偏好、潜能预选增删改查、vita 能量购买与项目、构筑改造/备注/生成/丢弃。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 4601 | `star_tower_apply_req` | `proto.StarTowerApplyReq` | 4602 `StarTowerApplyResp` | 4603 `Error` |
| 4607 | `star_tower_interact_req` | `proto.StarTowerInteractReq` | 4608 `StarTowerInteractResp` | 4609 `Error` |
| 4610 | `star_tower_info_req` | `proto.Nil` | 4611 `StarTowerInfo` | 4612 `Error` |
| 4613 | `star_tower_give_up_req` | `proto.Nil` | 4614 `StarTowerGiveUpResp` | 4615 `Error` |
| 4701 | `star_tower_build_whether_save_req` | `proto.StarTowerBuildWhetherSaveReq` | 4702 `StarTowerBuildWhetherSaveResp` | 4703 `Error` |
| 4704 | `star_tower_build_brief_list_get_req` | `proto.Nil` | 4705 `StarTowerBuildBriefListGetResp` | 4706 `Error` |
| 4707 | `star_tower_build_detail_get_req` | `proto.StarTowerBuildDetailGetReq` | 4708 `StarTowerBuildDetailGetResp` | 4709 `Error` |
| 4710 | `star_tower_build_delete_req` | `proto.StarTowerBuildDeleteReq` | 4711 `StarTowerBuildDeleteResp` | 4712 `Error` |
| 4713 | `star_tower_build_name_set_req` | `proto.StarTowerBuildNameSetReq` | 4714 `Nil` | 4715 `Error` |
| 4716 | `star_tower_build_lock_unlock_req` | `proto.StarTowerBuildLockUnlockReq` | 4717 `Nil` | 4718 `Error` |
| 4719 | `star_tower_build_preference_set_req` | `proto.StarTowerBuildPreferenceSetReq` | 4720 `Nil` | 4721 `Error` |
| 4725 | `potential_preselection_import_req` | `proto.PotentialPreselectionImportReq` | 4726 `PotentialPreselection` | 4727 `Error` |
| 4728 | `potential_preselection_name_set_req` | `proto.PotentialPreselectionNameSetReq` | 4729 `Nil` | 4730 `Error` |
| 4731 | `potential_preselection_preference_set_req` | `proto.PotentialPreselectionPreferenceSetReq` | 4732 `Nil` | 4733 `Error` |
| 4734 | `potential_preselection_update_req` | `proto.PotentialPreselectionUpdateReq` | 4735 `PotentialPreselection` | 4736 `Error` |
| 4737 | `potential_preselection_delete_req` | `proto.PotentialPreselectionDeleteReq` | 4738 `Nil` | 4739 `Error` |
| 4740 | `tower_vita_energy_buy_req` | `proto.UI32` | 4741 `TowerVitaEnergyBuyResp` | 4742 `Error` |
| 4743 | `tower_vita_project_list_req` | `proto.Nil` | 4744 `TowerVitaProjectListResp` | 4745 `Error` |
| 4746 | `tower_vita_project_update_req` | `proto.TowerVitaProjectUpdateRep` | 4747 `Nil` | 4748 `Error` |
| 4749 | `tower_vita_project_name_update_req` | `proto.TowerVitaProjectNameUpdateRep` | 4750 `Nil` | 4751 `Error` |
| 4752 | `tower_vita_project_set_req` | `proto.TowerVitaProjectSetRep` | 4753 `Nil` | 4754 `Error` |
| 4755 | `star_tower_build_potential_modify_req` | `proto.StarTowerBuildPotentialModifyReq` | 4756 `StarTowerBuildPotentialModifyResp` | 4757 `Error` |
| 4758 | `star_tower_build_note_modify_req` | `proto.StarTowerBuildNoteModifyReq` | 4759 `StarTowerBuildNoteModifyResp` | 4760 `Error` |
| 4761 | `star_tower_build_generator_req` | `proto.StarTowerBuildGeneratorReq` | 4762 `StarTowerBuildPotentialModifyResp` | 4763 `Error` |
| 4764 | `star_tower_build_discard_req` | `proto.StarTowerBuildDiscardReq` | 4765 `Nil` | 4766 `Error` |

### 构筑转换/星塔图鉴/好感（8）

构筑转换列表与提交、转换组奖励、星塔图鉴潜能、事件奖励、NPC 好感剧情奖励。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 4801 | `build_convert_detail_list_get_req` | `proto.Nil` | 4802 `BuildConvertDetailListGetResp` | 4803 `Error` |
| 4804 | `build_convert_submit_req` | `proto.BuildConvertSubmitReq` | 4805 `BuildConvertSubmitResp` | 4806 `Error` |
| 4807 | `build_convert_group_reward_receive_req` | `proto.UI32` | 4808 `BuildConvertGroupRewardReceiveResp` | 4809 `Error` |
| 4901 | `star_tower_book_potential_brief_list_get_req` | `proto.Nil` | 4902 `StarTowerBookPotentialBriefListResp` | 4903 `Error` |
| 4904 | `star_tower_book_char_potential_get_req` | `proto.UI32` | 4905 `StarTowerBookPotentialGetResp` | 4906 `Error` |
| 4907 | `star_tower_book_potential_reward_receive_req` | `proto.UI32` | 4908 `StarTowerBookPotentialRewardReceiveResp` | 4909 `Error` |
| 4913 | `star_tower_book_event_reward_receive_req` | `proto.UI32` | 4914 `StarTowerBookEventRewardReceiveResp` | 4915 `Error` |
| 4919 | `npc_affinity_plot_reward_receive_req` | `proto.UI32` | 4920 `NPCAffinityPlotRewardReceiveResp` | 4921 `Error` |

### 居民商店（2）

居民商店商品列表与购买。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 5010 | `resident_shop_get_req` | `proto.ResidentShopGetReq` | 5011 `ResidentShopGetResp` | 5012 `Error` |
| 5013 | `resident_shop_purchase_req` | `proto.ResidentShopPurchaseReq` | 5014 `ResidentShopPurchaseResp` | 5015 `Error` |

### 商城与月卡（11）

宝石商店、月卡、礼包、普通商店、活动商店、订单取消与领取。本地服没有支付/订单体系，需要假单模式。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 5101 | `mall_gem_list_req` | `proto.Nil` | 5102 `MallGemList` | 5103 `Error` |
| 5104 | `mall_gem_order_req` | `proto.String` | 5105 `OrderInfo` | 5106 `Error` |
| 5107 | `mall_order_cancel_req` | `proto.String` | 5108 `String` | 5109 `Error` |
| 5110 | `mall_order_collect_req` | `proto.String` | 5111 `CollectResp` | 5112 `CollectResp` |
| 5113 | `mall_monthlyCard_list_req` | `proto.Nil` | 5114 `MallMonthlyCardList` | 5115 `Error` |
| 5116 | `mall_monthlyCard_order_req` | `proto.String` | 5117 `OrderInfo` | 5118 `Error` |
| 5122 | `mall_package_order_req` | `proto.String` | 5123 `MallPackageOrder` | 5124 `Error` |
| 5125 | `mall_shop_list_req` | `proto.Nil` | 5126 `MallShopProductList` | 5127 `Error` |
| 5128 | `mall_shop_order_req` | `proto.MallShopOrderReq` | 5129 `ChangeInfo` | 5130 `Error` |
| 5131 | `mall_activity_list_req` | `proto.Nil` | 5132 `MallActivityList` | 5133 `Error` |
| 5134 | `mall_activity_order_req` | `proto.String` | 5135 `OrderInfo` | 5136 `Error` |

### 追猎（6）

追猎信息、申请、结算、推荐、BOSS 奖励、痕迹追踪。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 5201 | `trace_hunt_info_req` | `proto.Nil` | 5202 `TraceHuntInfo` | 5203 `Error` |
| 5204 | `trace_hunt_apply_req` | `proto.TraceHuntApplyReq` | 5205 `Nil` | 5206 `Error` |
| 5207 | `trace_hunt_settle_req` | `proto.TraceHuntSettleReq` | 5208 `TraceHuntSettleResp` | 5209 `Error` |
| 5210 | `trace_hunt_recommend_req` | `proto.Nil` | 5211 `TraceHuntRecommendResp` | 5212 `Error` |
| 5213 | `trace_hunt_boss_reward_receive_req` | `proto.Nil` | 5214 `TraceHuntBossRewardReceiveResp` | 5215 `Error` |
| 5216 | `trace_hunt_trace_req` | `proto.I32` | 5217 `TraceHuntTraceResp` | 5218 `Error` |

### 士兵系统（13）

士兵申请/信息/交互/放弃/离场/伙伴追踪、赛季奖励、学习、科技节点、战报收藏与最近列表。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 5301 | `soldier_apply_req` | `proto.SoldierApplyReq` | 5302 `SoldierApplyResp` | 5303 `Error` |
| 5304 | `soldier_info_req` | `proto.Nil` | 5305 `SoldierInfo` | 5306 `Error` |
| 5307 | `soldier_interact_req` | `proto.SoldierInteractReq` | 5308 `SoldierInteractResp` | 5309 `Error` |
| 5310 | `soldier_give_up_req` | `proto.Nil` | 5311 `SoldierSettleDataResp` | 5312 `Error` |
| 5313 | `soldier_step_out_req` | `proto.Nil` | 5314 `Nil` | 5315 `Error` |
| 5316 | `soldier_partner_trace_req` | `proto.SoldierPartnerTraceReq` | 5317 `Nil` | 5318 `Error` |
| 5322 | `soldier_season_reward_receive_req` | `proto.UI32` | 5323 `SoldierSeasonRewardReceiveResp` | 5324 `Error` |
| 5325 | `soldier_learn_req` | `proto.SoldierLearnReq` | 5326 `Nil` | 5327 `Error` |
| 5404 | `soldier_technology_node_unlock_req` | `proto.UI32` | 5405 `ChangeInfo` | 5406 `Error` |
| 5407 | `soldier_battle_report_favorite_req` | `proto.String` | 5408 `Nil` | 5409 `Error` |
| 5410 | `soldier_battle_report_unfavorite_req` | `proto.String` | 5411 `Nil` | 5412 `Error` |
| 5413 | `soldier_battle_report_favorite_list_req` | `proto.Nil` | 5414 `SoldierBattleReportFavoriteList` | 5415 `Error` |
| 5416 | `soldier_battle_report_recent_list_req` | `proto.Nil` | 5417 `SoldierBattleReportRecentList` | 5418 `Error` |

### 招募（7）

抽卡（6001）、历史、保底领取、新手抽取/保存/获取、奖励领取。抽卡概率与保底逻辑没有实现。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 6001 | `gacha_spin_req` | `proto.GachaSpinReq` | 6002 `GachaSpinResp` | 6003 `Error` |
| 6007 | `gacha_histories_req` | `proto.UI32` | 6008 `GachaHistories` | 6009 `Error` |
| 6010 | `gacha_guarantee_reward_receive_req` | `proto.UI32` | 6011 `ChangeInfo` | 6012 `Error` |
| 6013 | `gacha_newbie_spin_req` | `proto.UI32` | 6014 `GachaNewbieSpinResp` | 6015 `Error` |
| 6016 | `gacha_newbie_save_req` | `proto.GachaNewbieSaveReq` | 6017 `Nil` | 6018 `Error` |
| 6019 | `gacha_newbie_obtain_req` | `proto.GachaNewbieObtainReq` | 6020 `ChangeInfo` | 6021 `Error` |
| 6026 | `gacha_reward_get_req` | `proto.UI32` | 6027 `ChangeInfo` | 6028 `Error` |

### 塔罗牌（2）

塔罗牌详情与奖励领取。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 6101 | `tower_book_fate_card_detail_req` | `proto.Nil` | 6102 `TowerBookFateCardDetailResp` | 6103 `Error` |
| 6104 | `tower_book_fate_card_reward_receive_req` | `proto.TowerBookFateCardRewardReq` | 6105 `ChangeInfo` | 6106 `Error` |

### 联合演练（17）

两套联合演练（6201 段与 6301 段）的申请、继续、同步、放弃、撤退、结算、扫荡、排行、任务奖励。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 6201 | `joint_drill_apply_req` | `proto.JointDrillApplyReq` | 6202 `JointDrillApplyResp` | 6203 `Error` |
| 6204 | `joint_drill_continue_req` | `proto.JointDrillContinueReq` | 6205 `Nil` | 6206 `Error` |
| 6207 | `joint_drill_sync_req` | `proto.JointDrillSyncReq` | 6208 `Nil` | 6209 `Error` |
| 6210 | `joint_drill_give_up_req` | `proto.JointDrillGiveUpReq` | 6211 `JointDrillSettle` | 6212 `Error` |
| 6213 | `joint_drill_retreat_req` | `proto.Nil` | 6214 `Nil` | 6215 `Error` |
| 6216 | `joint_drill_settle_req` | `proto.JointDrillSettleReq` | 6217 `JointDrillSettle` | 6218 `Error` |
| 6219 | `joint_drill_game_over_req` | `proto.Nil` | 6220 `JointDrillSettle` | 6221 `Error` |
| 6222 | `joint_drill_sweep_req` | `proto.JointDrillSweepReq` | 6223 `JointDrillSweepResp` | 6224 `Error` |
| 6225 | `joint_drill_rank_req` | `proto.Nil` | 6226 `JointDrillRankInfo` | 6227 `Error` |
| 6228 | `joint_drill_quest_reward_receive_req` | `proto.Nil` | 6229 `ChangeInfo` | 6230 `Error` |
| 6301 | `joint_drill_2_apply_req` | `proto.JointDrill2ApplyReq` | 6302 `JointDrill2ApplyResp` | 6303 `Error` |
| 6304 | `joint_drill_2_continue_req` | `proto.JointDrill2ContinueReq` | 6305 `Nil` | 6306 `Error` |
| 6307 | `joint_drill_2_sync_req` | `proto.JointDrill2SyncReq` | 6308 `Nil` | 6309 `Error` |
| 6310 | `joint_drill_2_give_up_req` | `proto.JointDrill2GiveUpReq` | 6311 `JointDrillSettle` | 6312 `Error` |
| 6313 | `joint_drill_2_retreat_req` | `proto.Nil` | 6314 `Nil` | 6315 `Error` |
| 6316 | `joint_drill_2_settle_req` | `proto.JointDrill2SettleReq` | 6317 `JointDrillSettle` | 6318 `Error` |
| 6319 | `joint_drill_2_game_over_req` | `proto.Nil` | 6320 `JointDrillSettle` | 6321 `Error` |

### 副本与 BOSS（27）

剧情奖励、日常副本、宝石副本、区域 BOSS、旅者对决（含排行榜上传）、剧情关、技能副本、周 BOSS、教学关、剧情集。全部依赖战斗结算校验。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 7013 | `plot_reward_receive_req` | `proto.UI32` | 7014 `ChangeInfo` | 7015 `Error` |
| 7016 | `daily_instance_apply_req` | `proto.DailyInstanceApplyReq` | 7017 `Nil` | 7018 `Error` |
| 7019 | `daily_instance_settle_req` | `proto.DailyInstanceSettleReq` | 7020 `DailyInstanceSettleResp` | 7021 `Error` |
| 7022 | `daily_instance_raid_req` | `proto.DailyInstanceRaidReq` | 7023 `DailyInstanceRaidResp` | 7024 `Error` |
| 7028 | `char_gem_instance_apply_req` | `proto.CharGemInstanceApplyReq` | 7029 `ChangeInfo` | 7030 `Error` |
| 7031 | `char_gem_instance_settle_req` | `proto.CharGemInstanceSettleReq` | 7032 `CharGemInstanceSettleResp` | 7033 `Error` |
| 7034 | `char_gem_instance_sweep_req` | `proto.CharGemInstanceSweepReq` | 7035 `CharGemInstanceSweepResp` | 7036 `Error` |
| 7101 | `region_boss_level_apply_req` | `proto.RegionBossLevelApplyReq` | 7102 `ChangeInfo` | 7103 `Error` |
| 7104 | `region_boss_level_settle_req` | `proto.RegionBossLevelSettleReq` | 7105 `RegionBossLevelSettleResp` | 7106 `Error` |
| 7107 | `region_boss_level_sweep_req` | `proto.RegionBossLevelSweepReq` | 7108 `RegionBossLevelSweepResp` | 7109 `Error` |
| 7201 | `traveler_duel_level_apply_req` | `proto.TravelerDuelLevelApplyReq` | 7202 `Nil` | 7203 `Error` |
| 7204 | `traveler_duel_level_settle_req` | `proto.TravelerDuelLevelSettleReq` | 7205 `TravelerDuelLevelSettleResp` | 7206 `Error` |
| 7207 | `traveler_duel_info_req` | `proto.Nil` | 7208 `TravelerDuelInfo` | 7209 `Error` |
| 7210 | `traveler_duel_quest_reward_receive_req` | `proto.TravelerDuelQuestRewardReceiveReq` | 7211 `TravelerDuelQuestRewardReceiveResp` | 7212 `Error` |
| 7213 | `traveler_duel_rank_req` | `proto.Nil` | 7214 `TravelerDuelRankInfo` | 7215 `Error` |
| 7216 | `traveler_duel_rank_upload_req` | `proto.TravelerDuelBattleSamples` | 7217 `RankChange` | 7218 `Error` |
| 7301 | `story_apply_req` | `proto.StoryApplyReq` | 7302 `Nil` | 7303 `Error` |
| 7304 | `story_settle_req` | `proto.StorySettleReq` | 7305 `ChangeInfo` | 7306 `Error` |
| 7401 | `skill_instance_apply_req` | `proto.SkillInstanceApplyReq` | 7402 `ChangeInfo` | 7403 `Error` |
| 7404 | `skill_instance_settle_req` | `proto.SkillInstanceSettleReq` | 7405 `SkillInstanceSettleResp` | 7406 `Error` |
| 7407 | `skill_instance_sweep_req` | `proto.SkillInstanceSweepReq` | 7408 `SkillInstanceSweepResp` | 7409 `Error` |
| 7410 | `week_boss_apply_req` | `proto.WeekBossApplyReq` | 7411 `Nil` | 7412 `Error` |
| 7413 | `week_boss_settle_req` | `proto.WeekBossSettleReq` | 7414 `WeekBossLevelSettleResp` | 7415 `Error` |
| 7501 | `tutorial_level_settle_req` | `proto.UI32` | 7502 `Nil` | 7503 `Error` |
| 7504 | `tutorial_level_reward_receive_req` | `proto.UI32` | 7505 `ChangeInfo` | 7506 `Error` |
| 7601 | `story_set_info_req` | `proto.Nil` | 7602 `StorySetInfoResp` | 7603 `Error` |
| 7604 | `story_set_reward_receive_req` | `proto.StorySetRewardReceiveReq` | 7605 `ChangeInfo` | 7606 `Error` |

### 体力（3）

体力购买、体力提取、体力信息。初始资源发放后没有消耗入口。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 8001 | `energy_buy_req` | `proto.UI32` | 8002 `EnergyBuyResp` | 8003 `Error` |
| 8004 | `energy_extract_req` | `proto.UI32` | 8005 `ChangeInfo` | 8006 `Error` |
| 8007 | `energy_info_req` | `proto.Nil` | 8008 `Energy` | 8009 `Error` |

### 埋点上报（1）

`client_event_report_req`，模块直接丢弃即可，但需要显式接受避免 assert。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 8101 | `client_event_report_req` | `proto.Events` | 8102 `Nil` | 8103 `Error` |

### 吸血鬼幸存者与活动剧情（19）

幸存者玩法申请/区域切换/结算/奖励选择/宝箱/任务奖励/重开，天赋详情/重置/解锁/展示，活动剧情申请与结算、礼物投掷、最近阅读、历史剧情。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 8201 | `vampire_survivor_apply_req` | `proto.VampireSurvivorApplyReq` | 8202 `VampireSurvivorApplyResp` | 8203 `Error` |
| 8204 | `vampire_survivor_area_change_req` | `proto.VampireSurvivorAreaChangeReq` | 8205 `Nil` | 8206 `Error` |
| 8207 | `vampire_survivor_settle_req` | `proto.VampireSurvivorSettleReq` | 8208 `VampireSurvivorSettleResp` | 8209 `Error` |
| 8210 | `vampire_survivor_reward_select_req` | `proto.VampireSurvivorRewardSelectReq` | 8211 `VampireSurvivorRewardSelectResp` | 8212 `Error` |
| 8213 | `vampire_survivor_reward_chest_req` | `proto.VampireSurvivorRewardChestReq` | 8214 `VampireSurvivorRewardChestResp` | 8215 `Error` |
| 8216 | `vampire_survivor_quest_reward_receive_req` | `proto.VampireSurvivorQuestRewardReceiveReq` | 8217 `ChangeInfo` | 8218 `Error` |
| 8222 | `vampire_survivor_extra_reward_select_req` | `proto.VampireSurvivorRewardSelectReq` | 8223 `VampireSurvivorRewardSelectResp` | 8224 `Error` |
| 8225 | `vampire_survivor_restart_req` | `proto.Nil` | 8226 `Nil` | 8227 `Error` |
| 8301 | `vampire_talent_detail_req` | `proto.Nil` | 8302 `VampireTalentDetailResp` | 8303 `Error` |
| 8304 | `vampire_talent_reset_req` | `proto.Nil` | 8305 `Nil` | 8306 `Error` |
| 8307 | `vampire_talent_unlock_req` | `proto.UI32` | 8308 `Nil` | 8309 `Error` |
| 8310 | `vampire_talent_show_req` | `proto.Nil` | 8311 `Nil` | 8312 `Error` |
| 8313 | `vampire_survivor_info_req` | `proto.Nil` | 8314 `VampireSurvivorInfoResp` | 8315 `Error` |
| 8316 | `activity_story_apply_req` | `proto.ActivityStoryApplyReq` | 8317 `Nil` | 8318 `Error` |
| 8319 | `activity_story_settle_req` | `proto.ActivityStorySettleReq` | 8320 `ChangeInfo` | 8321 `Error` |
| 8322 | `activity_throw_gift_settle_req` | `proto.ActivityThrowGiftSettleReq` | 8323 `ChangeInfo` | 8324 `Error` |
| 8325 | `player_last_read_update_req` | `proto.LastRead` | 8326 `Nil` | 8327 `Error` |
| 8328 | `activity_history_story_apply_req` | `proto.ActivityHistoryStoryApplyReq` | 8329 `Nil` | 8330 `Error` |
| 8331 | `activity_history_story_settle_req` | `proto.ActivityHistoryStorySettleReq` | 8332 `ChangeInfo` | 8333 `Error` |

### 活动小游戏与引导任务（7）

企鹅卡、双倍任务、冰淇淋、无尽存档、引导任务领奖、数学跑酷。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 8401 | `activity_penguin_card_level_settle_req` | `proto.ActivityPenguinCardSettleReq` | 8402 `ChangeInfo` | 8403 `Error` |
| 8404 | `activity_penguin_card_quest_reward_receive_req` | `proto.ActivityPenguinCardQuestReceiveReq` | 8405 `ChangeInfo` | 8406 `Error` |
| 8407 | `activity_double_quest_reward_receive_req` | `proto.ActivityDoubleQuestRewardReq` | 8408 `ChangeInfo` | 8409 `Error` |
| 8411 | `activity_ice_cream_level_settle_req` | `proto.ActivityIceCreamLevelSettleReq` | 8412 `ActivityIceCreamLevelSettleSucceed` | 8413 `Error` |
| 8416 | `activity_penguin_card_endless_level_save_req` | `proto.ActivityPenguinCardEndlessLevelSaveReq` | 8417 `Nil` | 8418 `Error` |
| 8422 | `guide_mission_quest_reward_receive_req` | `proto.GuideMissionQuestRewardReceiveReq` | 8423 `ChangeInfo` | 8424 `Error` |
| 8430 | `activity_math_parkour_level_settle_req` | `proto.ActivityMathParkourLevelSettleReq` | 8431 `ActivityMathParkourLevelSettleSucceed` | 8432 `Error` |

### 回归活动（9）

回归信息、重逢奖励、任务点数/任务/登录/卡片奖励、回归礼包下单。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 8501 | `activity_returnee_info_req` | `proto.Nil` | 8502 `ActivityReturneeInfoResp` | 8503 `Error` |
| 8504 | `activity_returnee_reunion_reward_req` | `proto.Nil` | 8505 `ChangeInfo` | 8506 `Error` |
| 8507 | `activity_returnee_quest_point_reward_receive_req` | `proto.UI32` | 8508 `ChangeInfo` | 8509 `Error` |
| 8510 | `activity_returnee_avg_req` | `proto.Nil` | 8511 `Nil` | 8512 `Error` |
| 8513 | `activity_returnee_quest_reward_receive_req` | `proto.UI32` | 8514 `ChangeInfo` | 8515 `Error` |
| 8516 | `activity_returnee_login_reward_receive_req` | `proto.Nil` | 8517 `ChangeInfo` | 8518 `Error` |
| 8519 | `activity_returnee_card_reward_receive_req` | `proto.Nil` | 8520 `ChangeInfo` | 8521 `Error` |
| 8522 | `activity_returnee_card_order_req` | `proto.String` | 8523 `OrderInfo` | 8524 `Error` |
| 8525 | `activity_returnee_pack_order_req` | `proto.String` | 8526 `OrderInfo` | 8527 `Error` |

### 档案剧情（5）

档案剧情申请/结算/信息、邮件绑定奖励、章节通关。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 8601 | `activity_archive_story_apply_req` | `proto.ActivityArchiveStoryApplyReq` | 8602 `Nil` | 8603 `Error` |
| 8604 | `activity_archive_story_settle_req` | `proto.ActivityArchiveStorySettleReq` | 8605 `ChangeInfo` | 8606 `Error` |
| 8607 | `activity_archive_story_info_req` | `proto.UI32` | 8608 `ActivityHistoryStoryChapter` | 8609 `Error` |
| 8610 | `activity_mail_bind_reward_receive_req` | `proto.UI32` | 8611 `ChangeInfo` | 8612 `Error` |
| 8613 | `activity_archive_story_chapter_pass_req` | `proto.ActivityArchiveStoryChapterPassReq` | 8614 `Nil` | 8615 `Error` |

邮件的列表、已读、领取、删除、置顶 5 个请求本批已接入，使用 `game_mail` 持久化，因此不再列入未接入请求表。`mail_state_notify` 仍未下发，GM 发信后打开邮箱刷新可见。

### 运营活动（15）

周期奖励、登录奖励、塔防、跑酷对决、活动商城登录奖励。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 9104 | `activity_periodic_reward_receive_req` | `proto.ActivityPeriodicRewardReq` | 9105 `ChangeInfo` | 9106 `Error` |
| 9107 | `activity_periodic_final_reward_receive_req` | `proto.UI32` | 9108 `ChangeInfo` | 9109 `Error` |
| 9110 | `activity_login_reward_receive_req` | `proto.UI32` | 9111 `ChangeInfo` | 9112 `Error` |
| 9113 | `activity_tower_defense_story_reward_receive_req` | `proto.UI32` | 9114 `ChangeInfo` | 9115 `Error` |
| 9116 | `activity_tower_defense_quest_reward_receive_req` | `proto.ActivityTowerDefenseQuestReceiveReq` | 9117 `ChangeInfo` | 9118 `Error` |
| 9119 | `activity_tower_defense_level_apply_req` | `proto.ActivityTowerDefenseLevelApplyReq` | 9120 `Nil` | 9121 `Error` |
| 9122 | `activity_tower_defense_level_settle_req` | `proto.ActivityTowerDefenseLevelSettleReq` | 9123 `ChangeInfo` | 9124 `Error` |
| 9125 | `activity_trekker_versus_apply_req` | `proto.ActivityTrekkerVersusApplyReq` | 9126 `Nil` | 9127 `Error` |
| 9128 | `activity_trekker_versus_settle_req` | `proto.ActivityTrekkerVersusSettleReq` | 9129 `ActivityTrekkerVersusSettleResp` | 9130 `Error` |
| 9131 | `activity_trekker_versus_reward_receive_req` | `proto.UI32` | 9132 `ActivityTrekkerVersusRewardReceiveResp` | 9133 `Error` |
| 9134 | `activity_trekker_versus_idle_refresh_req` | `proto.UI32` | 9135 `TrekkerVersusShow` | 9136 `Error` |
| 9137 | `activity_trekker_versus_idle_reward_receive_req` | `proto.UI32` | 9138 `ActivityTrekkerVersusIdleRewardReceiveResp` | 9139 `Error` |
| 9140 | `activity_trekker_versus_rank_boost_req` | `proto.ActivityTrekkerVersusRankBoostReq` | 9141 `ActivityTrekkerVersusRankBoostResp` | 9142 `Error` |
| 9143 | `activity_trekker_versus_schedule_reward_receive_req` | `proto.ActivityTrekkerVersusScheduleRewardReceiveReq` | 9144 `ActivityTrekkerVersusScheduleRewardReceiveResp` | 9145 `Error` |
| 9146 | `activity_malllogin_reward_receive_req` | `proto.UI32` | 9147 `ChangeInfo` | 9148 `Error` |

### 通讯录与分享（3）

通讯录上报与置顶、分享奖励。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 9204 | `phone_contacts_report_req` | `proto.PhoneContactsReportReq` | 9205 `ChangeInfo` | 9206 `Error` |
| 9207 | `phone_contacts_top_req` | `proto.UI32` | 9208 `Nil` | 9209 `Error` |
| 9210 | `activity_share_reward_receive_req` | `proto.ActivityShareRewardReq` | 9211 `ChangeInfo` | 9212 `Error` |

### 天赋（5）

天赋解锁/重置/节点重置/背景/组解锁。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 9301 | `talent_unlock_req` | `proto.UI32` | 9302 `TalentUnlockResp` | 9303 `Error` |
| 9304 | `talent_reset_req` | `proto.TalentResetReq` | 9305 `ChangeInfo` | 9306 `Error` |
| 9307 | `talent_node_reset_req` | `proto.UI32` | 9308 `TalentNodeResetResp` | 9309 `Error` |
| 9310 | `talent_background_set_req` | `proto.TalentBackgroundSetReq` | 9311 `Nil` | 9312 `Error` |
| 9313 | `talent_group_unlock_req` | `proto.UI32` | 9314 `TalentGroupUnlockResp` | 9315 `Error` |

### 活动试炼（1）

活动试炼奖励领取。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 9401 | `activity_trial_reward_receive_req` | `proto.ActivityTrialRewardReceiveReq` | 9402 `ChangeInfo` | 9403 `Error` |

### 活动 CG 阅读（1）

活动 CG 阅读发奖，属于剧情类发奖链路。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 9501 | `activity_cg_read_req` | `proto.UI32` | 9502 `ChangeInfo` | 9503 `Error` |

### 宿舍（22）

宿舍信息、家具预设/背包/保存/删除、改名、邀请角色、奖励、角色互动、好感礼物、好友列表与拜访、布置奖励、BGM、点赞、房间场景、成就奖励、方块转换、变更数据、学习、家具数量。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 9510 | `dorm_info_list_req` | `proto.Nil` | 9511 `DormInfoListResp` | 9512 `Error` |
| 9513 | `dorm_furniture_preset_list_req` | `proto.Nil` | 9514 `DormFurniturePresetListResp` | 9515 `Error` |
| 9516 | `dorm_bag_list_req` | `proto.Nil` | 9517 `DormBagListResp` | 9518 `Error` |
| 9519 | `dorm_furniture_preset_delete_req` | `proto.UI32` | 9520 `Nil` | 9521 `Error` |
| 9522 | `dorm_furniture_save_req` | `proto.DormFurnitureSaveReq` | 9523 `I64` | 9524 `Error` |
| 9525 | `dorm_info_update_name_req` | `proto.StringV` | 9526 `Nil` | 9527 `Error` |
| 9528 | `dorm_invitation_role_req` | `proto.DormInvitationRoleReq` | 9529 `Nil` | 9530 `Error` |
| 9531 | `dorm_reward_receive_req` | `proto.DormRewardReceiveReq` | 9532 `ChangeInfo` | 9533 `Error` |
| 9534 | `dorm_role_interaction_req` | `proto.DormRoleInteractionReq` | 9535 `DormRoleInteractionResp` | 9536 `Error` |
| 9537 | `dorm_affinity_gift_send_req` | `proto.DormAffinityGiftSendReq` | 9538 `DormAffinityGiftSendResp` | 9539 `Error` |
| 9540 | `dorm_friend_list_req` | `proto.Nil` | 9541 `DormFriendListResp` | 9542 `Error` |
| 9543 | `dorm_friend_visit_req` | `proto.DormFriendVisitReq` | 9544 `DormFriendVisitResp` | 9545 `Error` |
| 9547 | `dorm_layout_reward_receive_req` | `proto.DormLayoutRewardReceiveReq` | 9548 `ChangeInfo` | 9549 `Error` |
| 9550 | `dorm_room_bgm_req` | `proto.DormRoomBGMReq` | 9551 `Nil` | 9552 `Error` |
| 9553 | `dorm_friend_like_req` | `proto.DormFriendLikeReq` | 9554 `Nil` | 9555 `Error` |
| 9556 | `dorm_update_preset_name_req` | `proto.StringV` | 9557 `Nil` | 9558 `Error` |
| 9559 | `dorm_set_room_scene_req` | `proto.UI32V` | 9560 `Nil` | 9561 `Error` |
| 9562 | `dorm_achievement_reward_receive_req` | `proto.DormAchievementRewardReceiveReq` | 9563 `AchievementComplete` | 9564 `Error` |
| 9565 | `dorm_blocks_convert_req` | `proto.UI32` | 9566 `ChangeInfo` | 9567 `Error` |
| 9568 | `dorm_get_change_data_req` | `proto.Nil` | 9569 `DormChangeDataResp` | 9570 `Error` |
| 9571 | `dorm_learn_req` | `proto.NewbieInfo` | 9572 `Nil` | 9573 `Error` |
| 9574 | `dorm_get_furniture_num_req` | `proto.UI32` | 9575 `ItemTpl` | 9576 `Error` |

### 活动关卡与战令（11）

活动关卡申请/结算/扫荡、活动平均奖励、活动商店购买、战令信息/领奖/买等级/下单/领取/任务奖励。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 9601 | `activity_levels_apply_req` | `proto.ActivityLevelsApplyReq` | 9602 `ChangeInfo` | 9603 `Error` |
| 9604 | `activity_levels_settle_req` | `proto.ActivityLevelsSettleReq` | 9605 `ActivityLevelsSettleResp` | 9606 `Error` |
| 9607 | `activity_levels_sweep_req` | `proto.ActivityLevelsSweepReq` | 9608 `ActivityLevelsSweepResp` | 9609 `Error` |
| 9701 | `activity_avg_reward_receive_req` | `proto.ActivityAvgRewardReceiveReq` | 9702 `ChangeInfo` | 9703 `Error` |
| 9751 | `activity_shop_purchase_req` | `proto.ActivityShopPurchaseReq` | 9752 `ActivityShopPurchaseResp` | 9753 `Error` |
| 9801 | `battle_pass_info_req` | `proto.Nil` | 9802 `BattlePassInfo` | 9803 `Error` |
| 9804 | `battle_pass_reward_receive_req` | `proto.BattlePassRewardReceiveReq` | 9805 `BattlePassRewardReceiveResp` | 9806 `Error` |
| 9807 | `battle_pass_level_buy_req` | `proto.UI32V` | 9808 `BattlePassLevelBuyResp` | 9809 `Error` |
| 9810 | `battle_pass_order_req` | `proto.BattlePassOrderReq` | 9811 `OrderInfo` | 9812 `Error` |
| 9813 | `battle_pass_order_collect_req` | `proto.String` | 9814 `BattlePassOrderCollectResp` | 9815 `CollectResp` |
| 9816 | `battle_pass_quest_reward_receive_req` | `proto.UI32` | 9817 `BattlePassQuestRewardResp` | 9818 `Error` |

### 兑换码（1）

兑换码校验与发奖。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 9901 | `redeem_code_req` | `proto.String` | 9902 `RedeemCodeResp` | 9903 `Error` |

### 活动（挖矿/积分 BOSS/饼干）（11）

挖矿申请/挖掘/下一层/剧情与任务奖励、积分 BOSS 申请/结算/排行/星级奖励、饼干活动结算与任务奖励。

| 请求 | 名称 | 请求体 | 成功回包 | 失败回包 |
| ---: | --- | --- | --- | --- |
| 11001 | `activity_mining_apply_req` | `proto.ActivityMiningApplyReq` | 11002 `ActivityMiningApplyResp` | 11003 `Error` |
| 11004 | `activity_mining_dig_req` | `proto.ActivityMiningDigReq` | 11005 `ActivityMiningDigResp` | 11006 `Error` |
| 11007 | `activity_mining_move_to_next_layer_req` | `proto.ActivityMiningMoveToNextLayerReq` | 11008 `ActivityMiningMoveToNextLayerResp` | 11009 `Error` |
| 11010 | `activity_mining_story_reward_receive_req` | `proto.ActivityMiningStoryRewardReceiveReq` | 11011 `ChangeInfo` | 11012 `Error` |
| 11013 | `activity_mining_quest_reward_receive_req` | `proto.ActivityMiningQuestRewardReceiveReq` | 11014 `ChangeInfo` | 11015 `Error` |
| 11101 | `score_boss_apply_req` | `proto.ScoreBossApplyReq` | 11102 `Nil` | 11103 `Error` |
| 11104 | `score_boss_settle_req` | `proto.ScoreBossSettleReq` | 11105 `ScoreBossSettleResp` | 11106 `Error` |
| 11107 | `score_boss_rank_req` | `proto.Nil` | 11108 `ScoreBossRankInfo` | 11109 `Error` |
| 11110 | `score_boss_star_reward_receive_req` | `proto.ScoreBossStarRewardReceiveReq` | 11111 `ChangeInfo` | 11112 `Error` |
| 11201 | `activity_cookie_settle_req` | `proto.ActivityCookieSettleReq` | 11202 `ChangeInfo` | 11203 `Error` |
| 11204 | `activity_cookie_quest_reward_receive_req` | `proto.ActivityCookieQuestRewardReceiveReq` | 11205 `ChangeInfo` | 11206 `Error` |

合计 **307** 个完全未接入请求；已接入请求的剩余失败包、抽卡同步与通知也计入顶部汇总。

## 四、未实现的通知（122 个）

客户端不会主动请求通知，全部由服务端下发。缺失会影响对应异步状态同步；成功回包中的 `ChangeInfo` 也能更新界面。GM 类为负数编号，正常联调可以永久不实现。唯一已实现的是 `10001 player_new_notify`（无存档时告知客户端去建角）。

### GM 指令类通知（57，可跳过）

`-10059` handbook_complete、`-10058` clear_all_activity_mathParkour_levels、`-10057` clear_all_activity_iceCream_levels、`-10056` sd_soldier_effect、`-10055` sd_soldier_info、`-10054` sd_shop_data、`-10053` sd_item_change、`-10052` sd_buff、`-10051` clear_all_activity_golden_spy_levels、`-10050` clear_all_activity_breakout_levels、`-10049` clear_story_set、`-10048` unlock_activity_story、`-10047` assist_add_build、`-10046` clear_all_activity_levels、`-10045` clear_all_activity_avg、`-10044` vs_add_fate_card、`-10043` char_gems_import、`-10042` char_gems_export、`-10040` clear_all_char_gem_instance、`-10039` change_npc_affinity、`-10038` tower_change_sub_note_skill、`-10037` world_class_quest_complete、`-10036` score_boss_level_reset、`-10035` score_boss_star_reward_reset、`-10034` char_up_change、`-10033` tower_growth_node_change、`-10032` st_harmony_skill、`-10031` clear_all_story、`-10030` story_complete、`-10029` infinity_tower_skip_floor、`-10028` infinity_tower_all_infos、`-10027` clear_all_equipment_instance、`-10026` st_import_build、`-10025` st_export_build、`-10024` st_add_new_case、`-10023` st_add_team_exp、`-10022` st_skip_floor、`-10021` st_clear_all_star_tower、`-10020` st_items_change、`-10019` char_affinity_final、`-10018` clear_all_skill_instance、`-10017` disc_reset、`-10016` clear_all_traveler_due、`-10015` clear_all_region_boss_level、`-10014` clear_all_week_boss、`-10013` clear_all_daily_instance、`-10012` refresh_agent、`-10011` star_tower_sub_note_skill_info、`-10010` vampire_survivor_talent_node、`-10009` add_vampire_season_score、`-10008` clear_all_vampire_survivor、`-10007` region_boss_level_final、`-10006` chars_final、`-10005` world_class_number、`-10004` char_change、`-10003` char_reset、`-10001` items_change

### 业务通知（65）

| 消息号 | 名称 | Protobuf |
| ---: | --- | --- |
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

## 五、还缺的零散应答号

| 消息号 | 名称 | 说明 |
| ---: | --- | --- |
| 6025 | `gacha_spin_sync_ack` | 抽卡同步包，实现 6001 时一起补 |
| 1003 | `player_data_failed_ack` | 1001 的失败回包；当前读档异常返回 HTTP 503，没有协议失败包 |
| 1014 | `player_ping_failed_ack` | 1012 的失败回包 |
| 4724 / 5121 / 5321 / 5403 / 6006 / 6024 / 8421 / 9103 / 9203 / 11115 | 各空壳请求的 `*_failed_ack` | 空壳模块补业务时一并接 |
| 10000 | `system_failed_ack` | 通用系统错误号尚未下发；现有业务错误使用各请求自己的失败号，未知请求回 HTTP 400 |

## 六、建议实施顺序

本批新增：个人页缺失 `Acc.SkinId` 的读取兼容与新角色默认值；`1006/1009/1027/1033/1036/1057`；`9001/9004/9007/9010/9013`。独立测试库的 GM 与游戏加密协议端到端验证已通过，个人信息页手机显示仍需重登复测。

1. **P0 可玩性**：实机核验 `1004→1002→本地序章→1015(1,-1)→大厅`，按日志定位卡点；`8419` 改用独立引导任务存档。`4601→4602` 属于普通星塔主循环，不能作为序章修复依据。
2. **P0 数据自洽**：`1101 item_use` + `ChangeInfo` 下发，把「发奖 / 消耗」链路先打通，后续所有奖励类协议都能复用。
3. **P1 大厅可用**：补 `9540` 宿舍好友、`8007` 体力信息，以及头像解锁台账；资料基础修改、基础好友 12 个请求与邮件读/领/删/置顶已接入。
4. **P1 星塔主循环**：`4610/4607/4704/4707/4725` 构筑与潜能，配 `1304/4919` 写操作。
5. **P2 玩法批次**：招募、副本结算、战令、活动——按实机日志里客户端真实发出的顺序推进，不要按编号顺序批量铺。
