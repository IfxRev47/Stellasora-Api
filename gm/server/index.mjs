import express from 'express';
import fs from 'node:fs';
import path from 'node:path';
import { dbPath, distDir, host, port, gatewayRoot } from './config.mjs';
import { Store } from './store.mjs';

const USERNAME_RE = /^[A-Za-z0-9_-]{3,32}$/;
const MAX_RESOURCES = 500;
const MAX_NEWBIES = 64;

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

const requireString = (value, field) => {
  if (typeof value !== 'string') throw new HttpError(400, `${field} 必须是字符串`);
  return value;
};

function assertUsername(value) {
  const username = requireString(value, '账号').trim();
  if (!USERNAME_RE.test(username)) {
    throw new HttpError(400, '账号需为 3-32 位字母、数字、下划线或短横线');
  }
  return username;
}

function assertPassword(value) {
  const password = requireString(value, '密码');
  const length = Array.from(password).length;
  if (length < 8 || length > 128) throw new HttpError(400, '密码需为 8-128 个字符');
  return password;
}

/** 与网关 1004 的校验保持一致：1-14 字、≤21 字节、不以数字开头。 */
function assertNickname(value) {
  const nickname = requireString(value, '昵称').trim();
  const chars = Array.from(nickname);
  if (chars.length === 0) throw new HttpError(400, '昵称不能为空');
  if (chars.length > 14) throw new HttpError(400, '昵称最多 14 个字符');
  if (Buffer.byteLength(nickname, 'utf8') > 21) throw new HttpError(400, '昵称最多 21 字节（UTF-8）');
  if (/^[0-9]/.test(nickname)) throw new HttpError(400, '昵称不能以数字开头');
  if (/[\p{Cc}\uFFFD]/u.test(nickname) || /^\p{Nd}/u.test(nickname)) throw new HttpError(400, '昵称包含非法字符');
  return nickname;
}

const toUint = (value, field, { min = 0, max = 0xffffffff } = {}) => {
  const number = Number(value);
  if (!Number.isInteger(number) || number < min || number > max) {
    throw new HttpError(400, `${field} 需为 ${min}-${max} 的整数`);
  }
  return number;
};

const store = new Store(dbPath);
const catalog = JSON.parse(fs.readFileSync(path.join(gatewayRoot, 'Fixtures/catalog.json'), 'utf8'));
const itemIndex = new Map(catalog.items.map(item => [item.id, item]));
const app = express();
app.disable('x-powered-by');
app.use(express.json({ limit: '2mb' }));
app.use('/api', (req, res, next) => { res.set('Cache-Control', 'no-store'); next(); });

app.get('/api/notices', (req, res) => res.json({ notices: store.notices() }));
function assertNotice(body = {}) {
  const category = requireString(body.category, '公告分类');
  if (!['system','activity','else_1','else_2'].includes(category)) throw new HttpError(400, '公告分类无效');
  const title = requireString(body.title, '标题').trim();
  const content = requireString(body.content, '正文').trim();
  if (!title || title.length > 100 || !content || content.length > 20000) throw new HttpError(400, '标题需 1-100 字，正文需 1-20000 字');
  if (typeof body.enabled !== 'boolean') throw new HttpError(400, '发布状态必须为布尔值');
  const startAt = toUint(body.startAt ?? 0, '开始时间', { max: 4102444800 });
  const endAt = toUint(body.endAt ?? 0, '结束时间', { max: 4102444800 });
  if (endAt && endAt <= startAt) throw new HttpError(400, '结束时间必须晚于开始时间');
  const sort = toUint(body.sort ?? 0, '排序', { max: 999999 });
  return { category, title, content, enabled: body.enabled, startAt, endAt, sort };
}
app.post('/api/notices', async (req, res, next) => {
  try { res.status(201).json(await store.saveNotice(null, assertNotice(req.body))); }
  catch (error) { next(error); }
});
app.put('/api/notices/:id', async (req, res, next) => {
  try { res.json(await store.saveNotice(toUint(req.params.id, '公告 ID', { min: 1 }), assertNotice(req.body))); }
  catch (error) { next(error); }
});
app.delete('/api/notices/:id', async (req, res, next) => {
  try { res.json(await store.deleteNotice(toUint(req.params.id, '公告 ID', { min: 1 }))); }
  catch (error) { next(error); }
});

app.get('/api/catalog/items', (req, res) => {
  const query = String(req.query.q || '').trim().toLowerCase();
  const result = catalog.items.filter(item => (!req.query.awardable || item.awardable) &&
    (!query || String(item.id).includes(query) || item.name.toLowerCase().includes(query)));
  res.json({ total: result.length, items: result.slice(0, 100) });
});

app.get('/api/registration/settings', (req, res) => res.json(store.registrationSettings()));
app.put('/api/registration/settings', async (req, res, next) => {
  try {
    if (typeof req.body?.requireCode !== 'boolean') throw new HttpError(400, 'requireCode 必须为布尔值');
    res.json(await store.setRegistrationSettings(req.body.requireCode));
  } catch (error) { next(error); }
});
app.get('/api/registration/codes', (req, res) => res.json({ codes: store.listCodes() }));
app.get('/api/registration/codes/:code/history', (req, res) => res.json({ history: store.codeHistory(req.params.code) }));
app.post('/api/registration/codes', async (req, res, next) => {
  try {
    const body = req.body || {};
    const count = toUint(body.count ?? 1, '生成数量', { min: 1, max: 100 });
    const maxUses = toUint(body.maxUses ?? 1, '使用次数', { min: 1, max: 10000 });
    const days = toUint(body.days ?? 7, '有效天数', { min: 1, max: 365 });
    const note = requireString(body.note ?? '', '备注');
    if (note.length > 200) throw new HttpError(400, '备注最多 200 字');
    res.status(201).json(await store.createCodes({ count, maxUses, expiresAt: Math.floor(Date.now()/1000)+days*86400, note }));
  } catch (error) { next(error); }
});
app.patch('/api/registration/codes/:code', async (req, res, next) => {
  try {
    if (typeof req.body?.disabled !== 'boolean') throw new HttpError(400, 'disabled 必须为布尔值');
    res.json(await store.setCodeDisabled(req.params.code, req.body.disabled));
  } catch (error) { next(error); }
});
app.post('/api/register', async (req, res, next) => {
  try {
    const body = req.body || {};
    res.status(201).json(await store.createAccount({ username: assertUsername(body.username),
      password: assertPassword(body.password), registrationCode: requireString(body.registrationCode ?? '', '注册码'), publicRegistration: true }));
  } catch (error) { next(error); }
});

app.get('/api/accounts/:uid/mails', (req, res) => res.json({ mails: store.mails(req.params.uid) }));
app.post('/api/accounts/:uid/mails', async (req, res, next) => {
  try {
    const body = req.body || {};
    const subject = requireString(body.subject, '标题').trim();
    const desc = requireString(body.desc, '内容').trim();
    const author = requireString(body.author ?? '本地 GM', '发件人').trim();
    if (!subject || subject.length > 100 || !desc || desc.length > 4000 || !author || author.length > 60)
      throw new HttpError(400, '标题需 1-100 字，内容需 1-4000 字，发件人需 1-60 字');
    const days = toUint(body.days ?? 30, '有效天数', { min: 1, max: 365 });
    if (!Array.isArray(body.attachments) || body.attachments.length > 50) throw new HttpError(400, '附件最多 50 条');
    const seen = new Set();
    const attachments = body.attachments.map(item => {
      const Tid = toUint(item.tid, '物品 ID', { min: 1 });
      const Qty = toUint(item.qty, '附件数量', { min: 1, max: 999999999 });
      if (!itemIndex.get(Tid)?.awardable) throw new HttpError(400, `物品 ${Tid} 暂不支持发放，仅支持资源和普通背包道具`);
      if (seen.has(Tid)) throw new HttpError(400, `物品 ${Tid} 重复`);
      seen.add(Tid);
      return { Tid, Qty };
    });
    res.status(201).json(await store.sendMail(req.params.uid, { subject, desc, author, days, attachments }));
  } catch (error) { next(error); }
});

app.get('/api/meta', (req, res) => {
  res.json(store.meta());
});

app.get('/api/accounts', (req, res) => {
  res.json({ accounts: store.listAccounts(String(req.query.q ?? '')) });
});

app.post('/api/accounts', async (req, res, next) => {
  try {
    const body = req.body ?? {};
    const username = assertUsername(body.username);
    const password = assertPassword(body.password);
    const nickname = body.nickname ? assertNickname(body.nickname) : null;
    const account = await store.createAccount({
      username,
      password,
      nickname,
      male: body.male !== false,
      registrationCode: body.registrationCode,
    });
    res.status(201).json(account);
  } catch (error) {
    next(error);
  }
});

app.patch('/api/accounts/:uid/password', async (req, res, next) => {
  try {
    const password = assertPassword(req.body?.password);
    res.json(await store.setPassword(req.params.uid, password));
  } catch (error) {
    next(error);
  }
});

app.delete('/api/accounts/:uid', async (req, res, next) => {
  try {
    res.json(await store.deleteAccount(req.params.uid));
  } catch (error) {
    next(error);
  }
});

app.get('/api/accounts/:uid', (req, res, next) => {
  try {
    const account = store.getAccountRow(req.params.uid);
    if (!account) throw new HttpError(404, `账号 ${req.params.uid} 不存在`);
    const player = store.getPlayer(req.params.uid);
    res.json({
      uid: String(account.uid),
      username: account.username,
      isTest: account.is_test === 1,
      hasCharacter: Boolean(player),
      player: player ? { ...player.json, _blobSize: player.blobSize, _updatedAt: player.updatedAt } : null,
    });
  } catch (error) {
    next(error);
  }
});

app.patch('/api/accounts/:uid/profile', async (req, res, next) => {
  try {
    const body = req.body ?? {};
    const patch = {};
    if (body.nickName !== undefined) patch.nickName = assertNickname(body.nickName);
    if (body.gender !== undefined) {
      if (typeof body.gender !== 'boolean') throw new HttpError(400, 'gender 必须是布尔值');
      patch.gender = body.gender;
    }
    if (body.signature !== undefined) {
      const signature = requireString(body.signature, '签名');
      if (Array.from(signature).length > 60) throw new HttpError(400, '签名最多 60 个字符');
      patch.signature = signature;
    }
    if (body.headIcon !== undefined) {
      patch.headIcon = toUint(body.headIcon, '头像 ID', { min: 1 });
      if (!catalog.heads.includes(patch.headIcon)) throw new HttpError(400, '头像 ID 不存在');
    }
    if (body.skinId !== undefined) patch.skinId = toUint(body.skinId, '皮肤 ID', { min: 1 });
    if (Object.keys(patch).length === 0) throw new HttpError(400, '没有要修改的字段');

    const result = await store.updatePlayer(req.params.uid, (player) => {
      player.Acc ??= {};
      if (patch.nickName !== undefined) player.Acc.NickName = patch.nickName;
      if (patch.gender !== undefined) player.Acc.Gender = patch.gender;
      if (patch.signature !== undefined) player.Acc.Signature = patch.signature;
      if (patch.headIcon !== undefined) player.Acc.HeadIcon = patch.headIcon;
      if (patch.skinId !== undefined) {
        const skin = catalog.skins[patch.skinId];
        if (!skin || !player.Chars?.some(c => c.Tid === skin.charId && (c.Skin === patch.skinId || c.Tid * 100 + 1 === patch.skinId)))
          throw new HttpError(400, '皮肤不存在或角色未拥有');
        player.Acc.SkinId = patch.skinId;
      }
    });
    res.json({ ok: true, ...result });
  } catch (error) {
    next(error);
  }
});

app.put('/api/accounts/:uid/resources', async (req, res, next) => {
  try {
    const items = req.body?.items;
    if (!Array.isArray(items)) throw new HttpError(400, 'items 必须是数组');
    if (items.length > MAX_RESOURCES) throw new HttpError(400, `资源条目最多 ${MAX_RESOURCES} 条`);

    const seen = new Set();
    const normalized = items.map((item) => {
      const tid = toUint(item?.tid ?? item?.Tid, '资源 ID', { min: 1 });
      const qty = toUint(item?.qty ?? item?.Qty, '数量', { min: 0, max: 999_999_999 });
      if (seen.has(tid)) throw new HttpError(400, `资源 ${tid} 重复`);
      seen.add(tid);
      return { Tid: tid, Qty: qty };
    });

    const result = await store.updatePlayer(req.params.uid, (player) => {
      player.Res = normalized;
    });
    res.json({ ok: true, count: normalized.length, ...result });
  } catch (error) {
    next(error);
  }
});

app.put('/api/accounts/:uid/newbies', async (req, res, next) => {
  try {
    const items = req.body?.items;
    if (!Array.isArray(items)) throw new HttpError(400, 'items 必须是数组');
    if (items.length > MAX_NEWBIES) throw new HttpError(400, `引导条目最多 ${MAX_NEWBIES} 条`);

    const seen = new Set();
    const normalized = items.map((item) => {
      const groupId = toUint(item?.groupId ?? item?.GroupId, '引导组', { min: 1, max: 64 });
      const stepId = toUint(item?.stepId ?? item?.StepId, '引导步骤', { min: -1, max: 10000 });
      if (seen.has(groupId)) throw new HttpError(400, `引导组 ${groupId} 重复`);
      seen.add(groupId);
      return { GroupId: groupId, StepId: stepId };
    });

    const result = await store.updatePlayer(req.params.uid, (player) => {
      player.Acc ??= {};
      player.Acc.Newbies = normalized;
    });
    res.json({ ok: true, count: normalized.length, ...result });
  } catch (error) {
    next(error);
  }
});

app.put('/api/accounts/:uid/board', async (req, res, next) => {
  try {
    const items = req.body?.items;
    if (!Array.isArray(items)) throw new HttpError(400, 'items 必须是数组');
    if (items.length > 8) throw new HttpError(400, '大厅展示最多 8 个角色');

    const seen = new Set();
    const normalized = items.map((value) => {
      const id = toUint(value, '展示 ID', { min: 1 });
      if (seen.has(id)) throw new HttpError(400, `展示 ID ${id} 重复`);
      seen.add(id);
      return id;
    });

    const result = await store.updatePlayer(req.params.uid, (player) => {
      player.Board = normalized;
    });
    res.json({ ok: true, count: normalized.length, ...result });
  } catch (error) {
    next(error);
  }
});

app.post('/api/accounts/:uid/player', async (req, res, next) => {
  try {
    const body = req.body ?? {};
    const nickname = assertNickname(body.nickName);
    res.status(201).json(await store.createPlayer(req.params.uid, { nickname, male: body.male !== false }));
  } catch (error) {
    next(error);
  }
});

app.delete('/api/accounts/:uid/player', async (req, res, next) => {
  try {
    res.json(await store.deletePlayer(req.params.uid));
  } catch (error) {
    next(error);
  }
});

app.use('/api', (req, res) => {
  res.status(404).json({ error: `未知接口 ${req.method} ${req.originalUrl}` });
});

if (fs.existsSync(path.join(distDir, 'index.html'))) {
  app.use(express.static(distDir));
  app.get('*', (req, res) => res.sendFile(path.join(distDir, 'index.html')));
}

app.use((error, req, res, next) => {
  const status = error.status ?? 500;
  if (status >= 500) console.error('[gm]', error);
  res.status(status).json({ error: error.message ?? '内部错误' });
});

app.listen(port, host, () => {
  console.log(`[gm] 数据库 ${dbPath}`);
  console.log(`[gm] 服务已启动 http://${host}:${port}`);
  if (!fs.existsSync(path.join(distDir, 'index.html'))) {
    console.log('[gm] 未找到 dist/，开发时请用 npm run dev（Vite 代理 /api）。');
  }
});
