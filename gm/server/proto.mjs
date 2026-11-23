import fs from 'node:fs';
import path from 'node:path';
import protobuf from 'protobufjs';
import { protoDir, seedDir, gatewayRoot } from './config.mjs';

const root = new protobuf.Root();
// 只加载 player_data.proto：它 public import 了 public.proto / public_dorm.proto，
// 足以覆盖 PlayerInfo 及其全部子消息，避免把 360 个协议文件都解析一遍。
root.loadSync(path.join(protoDir, 'player_data.proto'), { keepCase: true });

export const PlayerInfo = root.lookupType('proto.PlayerInfo');
export const Mail = root.lookupType('proto.Mail');

/** 与网关 JsonParser 一样按 proto3 JSON 读取种子模板。 */
const template = JSON.parse(fs.readFileSync(path.join(seedDir, 'player-info.json'), 'utf8'));

const catalog = JSON.parse(fs.readFileSync(path.join(gatewayRoot, 'Fixtures/catalog.json'), 'utf8'));
export const decodePlayer = (blob) => {
  const player = PlayerInfo.decode(new Uint8Array(blob));
  player.Acc ??= {};
  const owns = skin => {
    const character = catalog.skins[skin]?.charId;
    return character && player.Chars?.some(c => c.Tid === character && (c.Skin === skin || c.Tid * 100 + 1 === skin));
  };
  if (!owns(player.Acc.SkinId)) player.Acc.SkinId = (player.Chars || []).flatMap(c => [c.Skin, c.Tid * 100 + 1]).find(owns) || 0;
  if (!catalog.heads.includes(player.Acc.HeadIcon)) player.Acc.HeadIcon = 101;
  player.Acc.TitlePrefix ||= 1;
  player.Acc.TitleSuffix ||= 2;
  player.Titles ??= [];
  for (const TitleId of [1,2]) if (!player.Titles.some(x => x.TitleId === TitleId)) player.Titles.push({TitleId});
  return player;
};

export const encodePlayer = (message) => Buffer.from(PlayerInfo.encode(message).finish());

/** 转成可 JSON 化的普通对象；uint64 用字符串，避免精度丢失。 */
export const playerToJson = (message) =>
  PlayerInfo.toObject(message, { longs: String, enums: Number, bytes: String, arrays: true, objects: true });

/**
 * 按网关 NewPlayer 的规则克隆一份新存档：
 * UID / 昵称 / 性别 / 创建时间按请求覆盖，Hashtag 取 UID 末四位，
 * 引导组 1-3 预置 StepId=-1，供后续登录跳过本地序章。
 */
export function buildNewPlayer({ uid, nickname, male, now }) {
  const player = structuredClone(template);
  player.Acc ??= {};
  player.Acc.Id = String(uid);
  player.Acc.NickName = nickname;
  player.Acc.Gender = Boolean(male);
  player.Acc.CreateTime = String(now);
  player.Acc.Hashtag = Number(BigInt(uid) % 10000n);
  player.Acc.SkinId ||= player.Chars?.[0]?.Skin || 10301;
  player.Acc.Newbies = [1, 2, 3].map((GroupId) => ({ GroupId, StepId: -1 }));
  player.Board = [...(template.Board ?? [])];
  player.ServerTs = 0;
  return player;
}
