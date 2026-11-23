CREATE TABLE IF NOT EXISTS account_security (
    uid TEXT PRIMARY KEY REFERENCES accounts(uid) ON DELETE CASCADE,
    password_revision INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS registration_settings (
    id INTEGER PRIMARY KEY CHECK(id = 1),
    require_code INTEGER NOT NULL DEFAULT 0 CHECK(require_code IN (0,1))
);
INSERT OR IGNORE INTO registration_settings(id, require_code) VALUES(1,0);
CREATE TABLE IF NOT EXISTS registration_codes (
    code TEXT PRIMARY KEY,
    max_uses INTEGER NOT NULL CHECK(max_uses > 0),
    uses INTEGER NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    disabled INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL,
    note TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS registration_redemptions (
    code TEXT NOT NULL REFERENCES registration_codes(code),
    uid TEXT NOT NULL REFERENCES accounts(uid) ON DELETE CASCADE,
    used_at INTEGER NOT NULL,
    PRIMARY KEY(code, uid)
);
CREATE TABLE IF NOT EXISTS game_mail (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL REFERENCES accounts(uid) ON DELETE CASCADE,
    mail BLOB NOT NULL,
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS game_mail_uid ON game_mail(uid, id);
CREATE TABLE IF NOT EXISTS sdk_tickets (
    token_hash TEXT PRIMARY KEY,
    uid TEXT NOT NULL REFERENCES accounts(uid) ON DELETE CASCADE,
    expires_at INTEGER NOT NULL,
    password_revision INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS friend_links (
    owner TEXT NOT NULL REFERENCES players(uid) ON DELETE CASCADE,
    target TEXT NOT NULL REFERENCES players(uid) ON DELETE CASCADE,
    star INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(owner,target), CHECK(owner<>target)
);
CREATE TABLE IF NOT EXISTS friend_invites (
    sender TEXT NOT NULL REFERENCES players(uid) ON DELETE CASCADE,
    target TEXT NOT NULL REFERENCES players(uid) ON DELETE CASCADE,
    created_at INTEGER NOT NULL,
    PRIMARY KEY(sender,target), CHECK(sender<>target)
);
CREATE TABLE IF NOT EXISTS friend_energy (
    sender TEXT NOT NULL REFERENCES accounts(uid) ON DELETE CASCADE,
    target TEXT NOT NULL REFERENCES accounts(uid) ON DELETE CASCADE,
    day INTEGER NOT NULL,
    claimed INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(sender,target,day)
);
CREATE TABLE IF NOT EXISTS player_presence (
    uid TEXT PRIMARY KEY REFERENCES accounts(uid) ON DELETE CASCADE,
    last_login_ns INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS game_notices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    category TEXT NOT NULL CHECK(category IN ('system','activity','else_1','else_2')),
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
    start_at INTEGER NOT NULL DEFAULT 0,
    end_at INTEGER NOT NULL DEFAULT 0,
    sort INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER NOT NULL DEFAULT 0,
    CHECK(id NOT IN (1,2) OR (enabled=1 AND start_at=0 AND end_at=0)),
    CHECK(id<>1 OR category='system'),
    CHECK(id<>2 OR category='activity')
);
INSERT OR IGNORE INTO game_notices(id,category,title,content,sort) VALUES
    (1,'system','本地服务说明','欢迎来到星塔旅人本地测试服。当前已接入账号、个人资料与邮件等基础功能；部分玩法仍在开发中。',100),
    (2,'activity','邮件与奖励说明','GM 下发的奖励可在游戏邮箱中查看并领取。发信后请重新打开邮箱刷新；资源与普通道具附件领取后立即入账。',100);
