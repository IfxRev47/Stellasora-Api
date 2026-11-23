import fs from 'node:fs';
import crypto from 'node:crypto';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { dbPath, gatewayRoot } from './config.mjs';
import { buildNewPlayer, decodePlayer, encodePlayer, playerToJson, Mail } from './proto.mjs';

/** 与网关切页保持一致：PBKDF2-SHA256 / 210000 轮 / 32 字节，盐 16 字节。 */
const PASSWORD_ITERATIONS = 210_000;
const SCHEMA_VERSION = 2;
const BUSY_RETRIES = 6;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const isBusy = (error) => /SQLITE_BUSY|database is locked/i.test(String(error?.message ?? error));

export class Store {
  constructor(file = dbPath) {
    if (!fs.existsSync(file)) {
      throw new Error(`数据库不存在：${file}\n先启动一次本地网关，让它建库并写入预置测试账号。`);
    }
    this.file = file;
    this.db = new DatabaseSync(file);
    this.db.exec('PRAGMA busy_timeout = 5000');
    this.db.exec('PRAGMA foreign_keys = ON');
    this.migrate();
    this.db.exec(fs.readFileSync(path.join(gatewayRoot, 'Persistence/extensions.sql'), 'utf8'));
    this.columns = this.readColumns();
  }

  /** 只做网关同款 v1 → v2 迁移（加时间戳列）。v0 交给网关建库，避免 GM 造出半成品库。 */
  migrate() {
    const version = Number(this.db.prepare('PRAGMA user_version').get().user_version);
    if (version === 0) {
      throw new Error('数据库尚未初始化（user_version=0）。请先启动一次本地网关。');
    }
    if (version > SCHEMA_VERSION) {
      throw new Error(`数据库版本 ${version} 高于 GM 支持的 ${SCHEMA_VERSION}，请升级 GM。`);
    }
    if (version === SCHEMA_VERSION) return;

    // 构造函数里同步执行：迁移只有两条 ALTER，失败就让 GM 直接退出，避免半迁移状态。
    this.db.exec('BEGIN IMMEDIATE');
    try {
      this.db.exec(`
        ALTER TABLE accounts ADD COLUMN created_at INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE players ADD COLUMN updated_at INTEGER NOT NULL DEFAULT 0;
        PRAGMA user_version = 2;
      `);
      this.db.exec('COMMIT');
      this.migrated = true;
    } catch (error) {
      try {
        this.db.exec('ROLLBACK');
      } catch {
        // 保留原始错误。
      }
      throw error;
    }
  }

  readColumns() {
    const columns = new Set(
      this.db.prepare('PRAGMA table_info(accounts)').all().map((row) => row.name),
    );
    return { hasCreatedAt: columns.has('created_at') };
  }

  close() {
    this.db.close();
  }

  /** BEGIN IMMEDIATE：先拿写锁再读旧值，避免和网关或另一个 GM 会话互相覆盖。 */
  async tx(work, options = {}) {
    for (let attempt = 0; ; attempt += 1) {
      try {
        this.db.exec('BEGIN IMMEDIATE');
      } catch (error) {
        if (isBusy(error) && attempt < BUSY_RETRIES) {
          await sleep(40 * (attempt + 1));
          continue;
        }
        throw error;
      }
      try {
        const result = work();
        this.db.exec('COMMIT');
        return result;
      } catch (error) {
        try {
          this.db.exec('ROLLBACK');
        } catch {
          // 回滚失败说明事务已结束，保留原始错误。
        }
        if (isBusy(error) && attempt < BUSY_RETRIES) {
          await sleep(40 * (attempt + 1));
          continue;
        }
        throw error;
      }
    }
  }

  meta() {
    const accounts = this.db.prepare('SELECT COUNT(*) AS n FROM accounts').get().n;
    const players = this.db.prepare('SELECT COUNT(*) AS n FROM players').get().n;
    return {
      database: this.file,
      schemaVersion: Number(this.db.prepare('PRAGMA user_version').get().user_version),
      accounts,
      players,
      migrated: Boolean(this.migrated),
    };
  }

  listAccounts(query = '') {
    const keyword = `%${query.trim()}%`;
    const rows = this.db
      .prepare(
        `SELECT a.uid, a.username, a.is_test, ${this.columns.hasCreatedAt ? 'a.created_at' : '0 AS created_at'},
                p.player_info, p.updated_at
           FROM accounts a
           LEFT JOIN players p ON p.uid = a.uid
          WHERE (? = '%%' OR a.username LIKE ? OR a.uid LIKE ?)
          ORDER BY CAST(a.uid AS INTEGER)`,
      )
      .all(keyword, keyword, keyword);

    return rows.map((row) => {
      const summary = { uid: row.uid, username: row.username, isTest: row.is_test === 1 };
      summary.createdAt = Number(row.created_at) || null;
      summary.hasCharacter = row.player_info != null;
      summary.updatedAt = Number(row.updated_at) || null;
      if (row.player_info != null) {
        try {
          const player = decodePlayer(row.player_info);
          summary.nickname = player.Acc?.NickName ?? '';
          summary.headIcon = Number(player.Acc?.HeadIcon ?? 0);
          summary.charIds = (player.Acc?.Chars ?? []).map((item) => Number(item.CharId));
          summary.level = (player.Acc?.Chars ?? []).reduce(
            (max, item) => Math.max(max, Number(item.Level ?? 0)),
            0,
          );
          summary.newbies = (player.Acc?.Newbies ?? []).map((item) => ({
            groupId: Number(item.GroupId),
            stepId: Number(item.StepId),
          }));
          summary.resCount = (player.Res ?? []).length;
          summary.blobSize = row.player_info.length;
        } catch (error) {
          summary.decodeError = error.message;
        }
      }
      return summary;
    });
  }

  findAccount(username) {
    return this.db.prepare('SELECT uid, username, is_test FROM accounts WHERE username = ?').get(username);
  }

  async createAccount({ username, password, nickname, male = true, registrationCode, publicRegistration = false }) {
    const salt = crypto.randomBytes(16);
    const hash = crypto.pbkdf2Sync(password, salt, PASSWORD_ITERATIONS, 32, 'sha256');
    const now = Math.floor(Date.now() / 1000);

    return this.tx(() => {
      if (this.findAccount(username)) {
        const error = new Error(`账号 ${username} 已存在`);
        error.status = 409;
        throw error;
      }
      const maxUid = this.db
        .prepare('SELECT COALESCE(MAX(CAST(uid AS INTEGER)), 10001) AS max FROM accounts')
        .get().max;
      const uid = String(Math.max(Number(maxUid) + 1, 10002));

      this.db
        .prepare(
          `INSERT INTO accounts (uid, username, password_salt, password_hash, is_test, created_at)
           VALUES (?, ?, ?, ?, 0, ?)`,
        )
        .run(uid, username, new Uint8Array(salt), new Uint8Array(hash), now);

      if (publicRegistration || registrationCode) this.redeemCode(uid, registrationCode, now);

      if (nickname) {
        const player = buildNewPlayer({ uid, nickname, male, now });
        this.db
          .prepare('INSERT INTO players (uid, player_info, updated_at) VALUES (?, ?, ?)')
          .run(uid, new Uint8Array(encodePlayer(player)), now);
      }
      return { uid, username, hasCharacter: Boolean(nickname) };
    });
  }

  async setPassword(uid, password) {
    const account = this.getAccountRow(uid);
    if (!account) {
      const error = new Error(`账号 ${uid} 不存在`);
      error.status = 404;
      throw error;
    }
    const salt = crypto.randomBytes(16);
    const hash = crypto.pbkdf2Sync(password, salt, PASSWORD_ITERATIONS, 32, 'sha256');
    await this.tx(() => {
      this.db
        .prepare('UPDATE accounts SET password_salt = ?, password_hash = ? WHERE uid = ?')
        .run(new Uint8Array(salt), new Uint8Array(hash), String(uid));
      this.db.prepare(`INSERT INTO account_security(uid,password_revision) VALUES(?,1)
        ON CONFLICT(uid) DO UPDATE SET password_revision=password_revision+1`).run(String(uid));
    });
    return { ok: true, uid: String(uid), username: account.username, message: '密码已重置，请使用账号密码重新登录；SDK 已缓存的会话不代表密码校验' };
  }

  async deleteAccount(uid) {
    const account = this.getAccountRow(uid);
    if (!account) {
      const error = new Error(`账号 ${uid} 不存在`);
      error.status = 404;
      throw error;
    }
    if (account.is_test === 1) {
      const error = new Error('预置测试账号不允许删除');
      error.status = 403;
      throw error;
    }
    await this.tx(() => {
      this.db.prepare('DELETE FROM players WHERE uid = ?').run(String(uid));
      this.db.prepare('DELETE FROM accounts WHERE uid = ?').run(String(uid));
    });
    return { uid: String(uid), username: account.username };
  }

  getAccountRow(uid) {
    return this.db.prepare('SELECT uid, username, is_test FROM accounts WHERE uid = ?').get(String(uid));
  }

  /** 读存档：返回 protobuf 消息、JSON 视图和原始大小。 */
  getPlayer(uid) {
    const row = this.db
      .prepare('SELECT player_info, updated_at FROM players WHERE uid = ?')
      .get(String(uid));
    if (!row) return null;
    const message = decodePlayer(row.player_info);
    return {
      message,
      json: playerToJson(message),
      blobSize: row.player_info.length,
      updatedAt: Number(row.updated_at) || null,
    };
  }

  /** 读-改-写；回调抛错时整次修改回滚。写回前清掉响应专用字段 ServerTs。 */
  async updatePlayer(uid, mutate) {
    return this.tx(() => {
      const row = this.db
        .prepare('SELECT player_info FROM players WHERE uid = ?')
        .get(String(uid));
      if (!row) {
        const error = new Error(`UID ${uid} 还没有角色存档`);
        error.status = 404;
        throw error;
      }
      const message = decodePlayer(row.player_info);
      mutate(message);
      message.ServerTs = 0;
      const buf = encodePlayer(message);
      this.db
        .prepare('UPDATE players SET player_info = ?, updated_at = ? WHERE uid = ?')
        .run(new Uint8Array(buf), Math.floor(Date.now() / 1000), String(uid));
      return { blobSize: buf.length, json: playerToJson(message) };
    });
  }

  /** 给已有账号补建角色存档，走与网关 NewPlayer 相同的模板。 */
  async createPlayer(uid, { nickname, male = true }) {
    const now = Math.floor(Date.now() / 1000);
    return this.tx(() => {
      const account = this.getAccountRow(uid);
      if (!account) {
        const error = new Error(`账号 ${uid} 不存在`);
        error.status = 404;
        throw error;
      }
      const existing = this.db.prepare('SELECT 1 FROM players WHERE uid = ?').get(String(uid));
      if (existing) {
        const error = new Error(`UID ${uid} 已经有角色存档`);
        error.status = 409;
        throw error;
      }
      const player = buildNewPlayer({ uid: String(uid), nickname, male, now });
      const buf = encodePlayer(player);
      this.db
        .prepare('INSERT INTO players (uid, player_info, updated_at) VALUES (?, ?, ?)')
        .run(String(uid), new Uint8Array(buf), now);
      return { uid: String(uid), blobSize: buf.length };
    });
  }

  /** 删除角色存档但保留账号，用来重测建角流程。 */
  async deletePlayer(uid) {
    const result = this.db.prepare('DELETE FROM players WHERE uid = ?').run(String(uid));
    if (Number(result.changes) === 0) {
      const error = new Error(`UID ${uid} 没有可删除的存档`);
      error.status = 404;
      throw error;
    }
    return { uid: String(uid) };
  }

  registrationSettings() {
    return { requireCode: this.db.prepare('SELECT require_code FROM registration_settings WHERE id=1').get().require_code === 1 };
  }

  redeemCode(uid, raw, now) {
    const code = String(raw || '').trim().toUpperCase();
    if (!code && !this.registrationSettings().requireCode) return;
    const changed = this.db.prepare(`UPDATE registration_codes SET uses=uses+1 WHERE code=? AND disabled=0
      AND uses<max_uses AND (expires_at=0 OR expires_at>?)`).run(code, now);
    if (Number(changed.changes) !== 1) throw Object.assign(new Error('注册码无效、已过期、已停用或次数已用完'), { status: 400 });
    this.db.prepare('INSERT INTO registration_redemptions(code,uid,used_at) VALUES(?,?,?)').run(code, uid, now);
  }

  async setRegistrationSettings(requireCode) {
    await this.tx(() => this.db.prepare('UPDATE registration_settings SET require_code=? WHERE id=1').run(requireCode ? 1 : 0));
    return this.registrationSettings();
  }

  listCodes() {
    return this.db.prepare('SELECT * FROM registration_codes ORDER BY created_at DESC, code LIMIT 500').all();
  }

  codeHistory(code) {
    return this.db.prepare(`SELECT r.uid,a.username,r.used_at FROM registration_redemptions r
      JOIN accounts a ON a.uid=r.uid WHERE r.code=? ORDER BY used_at DESC`).all(code);
  }

  async createCodes({ count, maxUses, expiresAt, note }) {
    const now = Math.floor(Date.now() / 1000);
    return this.tx(() => {
      const codes = [];
      for (let i = 0; i < count; i++) {
        const code = crypto.randomBytes(10).toString('hex').toUpperCase();
        this.db.prepare('INSERT INTO registration_codes(code,max_uses,expires_at,created_at,note) VALUES(?,?,?,?,?)')
          .run(code, maxUses, expiresAt, now, note);
        codes.push(code);
      }
      return { codes };
    });
  }

  async setCodeDisabled(code, disabled) {
    return this.tx(() => {
      const result = this.db.prepare('UPDATE registration_codes SET disabled=? WHERE code=?').run(disabled ? 1 : 0, code);
      if (!Number(result.changes)) throw Object.assign(new Error('注册码不存在'), { status: 404 });
      return { ok: true };
    });
  }

  mails(uid) {
    return this.db.prepare('SELECT mail FROM game_mail WHERE uid=? ORDER BY id DESC').all(String(uid))
      .map(row => Mail.toObject(Mail.decode(row.mail), { longs: String, arrays: true, defaults: true }));
  }

  async sendMail(uid, { subject, desc, author, days, attachments }) {
    return this.tx(() => {
      if (!this.getPlayer(uid)) throw Object.assign(new Error('请先为账号创建角色'), { status: 404 });
      const now = Math.floor(Date.now() / 1000);
      const row = this.db.prepare('INSERT INTO game_mail(uid,mail,created_at) VALUES(?,?,?)').run(String(uid), new Uint8Array(), now);
      const id = Number(row.lastInsertRowid);
      if (id > 0xffffffff) throw new Error('邮件 ID 已超出协议范围');
      const mail = Mail.fromObject({ Id: id, Subject: subject, Desc: desc, Author: author,
        Time: now, Deadline: now + days * 86400, Flag: String(id), Attachments: attachments });
      this.db.prepare('UPDATE game_mail SET mail=? WHERE id=?').run(Mail.encode(mail).finish(), id);
      return { ok: true, id };
    });
  }

  notices() {
    return this.db.prepare('SELECT * FROM game_notices ORDER BY sort DESC,id DESC').all();
  }

  async saveNotice(id, notice) {
    return this.tx(() => {
      const { category, title, content, enabled, startAt, endAt, sort } = notice;
      const now = Date.now();
      if (id) {
        const existing = this.db.prepare('SELECT id FROM game_notices WHERE id=?').get(id);
        if (!existing) throw Object.assign(new Error('公告不存在'), { status: 404 });
        if ((id === 1 || id === 2) && (!enabled || startAt || endAt || category !== (id === 1 ? 'system' : 'activity')))
          throw Object.assign(new Error('两条常驻公告用于保证客户端分类完整，可修改标题、正文和排序'), { status: 400 });
        this.db.prepare(`UPDATE game_notices SET category=?,title=?,content=?,enabled=?,start_at=?,end_at=?,sort=?,updated_at=? WHERE id=?`)
          .run(category,title,content,enabled ? 1 : 0,startAt,endAt,sort,now,id);
      } else {
        id = Number(this.db.prepare(`INSERT INTO game_notices(category,title,content,enabled,start_at,end_at,sort,updated_at)
          VALUES(?,?,?,?,?,?,?,?)`).run(category,title,content,enabled ? 1 : 0,startAt,endAt,sort,now).lastInsertRowid);
      }
      return { ok: true, id };
    });
  }

  async deleteNotice(id) {
    if (id === 1 || id === 2) throw Object.assign(new Error('常驻分类公告不能删除，可编辑正文'), { status: 400 });
    return this.tx(() => {
      const result = this.db.prepare('DELETE FROM game_notices WHERE id=?').run(id);
      if (!Number(result.changes)) throw Object.assign(new Error('公告不存在'), { status: 404 });
      return { ok: true };
    });
  }
}
