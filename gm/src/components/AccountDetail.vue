<script setup>
import { computed, reactive, ref } from 'vue';
import { api } from '../api';

const props = defineProps({
  detail: { type: Object, required: true },
  section: { type: String, default: 'accounts' },
});
const emit = defineEmits(['saved', 'notify', 'deleted']);

const player = computed(() => props.detail.player);
const acc = computed(() => player.value?.Acc ?? {});
const initial = computed(() => (acc.value.NickName || props.detail.username || '?').slice(0, 1));

const saving = ref('');

const profile = reactive({
  nickName: acc.value.NickName ?? '',
  signature: acc.value.Signature ?? '',
  gender: Boolean(acc.value.Gender),
  headIcon: Number(acc.value.HeadIcon ?? 0),
  skinId: Number(acc.value.SkinId ?? 0),
});

const resources = ref((player.value?.Res ?? []).map((item) => ({ tid: Number(item.Tid), qty: Number(item.Qty) })));
const newbies = ref((acc.value.Newbies ?? []).map((item) => ({ groupId: Number(item.GroupId), stepId: Number(item.StepId) })));
const board = ref((player.value?.Board ?? []).map(Number));

const boardInput = ref('');
const password = ref('');
const createForm = reactive({ nickName: '', male: true });
const deleteConfirm = ref('');
const showRaw = ref(false);

const rawJson = computed(() => (player.value ? JSON.stringify(player.value, null, 2) : ''));

async function run(key, action, message) {
  if (saving.value) return;
  saving.value = key;
  try {
    await action();
    emit('notify', message);
    emit('saved');
    return true;
  } catch (error) {
    emit('notify', error.message, 'error');
    return false;
  } finally {
    saving.value = '';
  }
}

const saveProfile = () =>
  run(
    'profile',
    () =>
      api.patchProfile(props.detail.uid, {
        nickName: profile.nickName,
        signature: profile.signature,
        gender: profile.gender,
        headIcon: Number(profile.headIcon) || undefined,
        skinId: Number(profile.skinId) || undefined,
      }),
    '角色资料已保存',
  );

const saveResources = () =>
  run(
    'res',
    () =>
      api.saveResources(
        props.detail.uid,
        resources.value.map((item) => ({ tid: Number(item.tid), qty: Number(item.qty) })),
      ),
    '资源已保存',
  );

const saveNewbies = () =>
  run(
    'newbie',
    () =>
      api.saveNewbies(
        props.detail.uid,
        newbies.value.map((item) => ({
          groupId: Number(item.groupId),
          stepId: Number(item.stepId),
        })),
      ),
    '引导进度已保存',
  );

const skipAllNewbies = () => {
  newbies.value = [1, 2, 3].map((groupId) => ({ groupId, stepId: -1 }));
};

const saveBoard = () =>
  run('board', () => api.saveBoard(props.detail.uid, board.value.map(Number)), '展示角色已保存');

const addBoard = () => {
  const id = Number(boardInput.value);
  if (!Number.isInteger(id) || id <= 0) {
    emit('notify', '展示 ID 需为正整数', 'error');
    return;
  }
  if (board.value.includes(id)) {
    emit('notify', '该展示 ID 已存在', 'error');
    return;
  }
  if (board.value.length >= 8) {
    emit('notify', '最多 8 个展示角色', 'error');
    return;
  }
  board.value.push(id);
  boardInput.value = '';
};

const addResource = () => {
  resources.value.push({ tid: 0, qty: 0 });
};

const resetPassword = () => {
  if (password.value.length < 8) {
    emit('notify', '密码至少 8 位', 'error');
    return;
  }
  run('pwd', () => api.setPassword(props.detail.uid, password.value), '密码已重置，请退出登录后用新密码登录；自动登录使用的是已缓存票据').then((ok) => {
    if (ok) password.value = '';
  });
};

const createCharacter = () =>
  run('createChar', () => api.createPlayer(props.detail.uid, { ...createForm }), '角色已创建');

const deletePlayer = () => {
  if (!window.confirm(`确定删除 UID ${props.detail.uid} 的角色存档？账号会保留，游戏内需重新创建角色。`)) return;
  run('delPlayer', () => api.deletePlayer(props.detail.uid), '角色存档已删除');
};

const deleteAccount = async () => {
  if (deleteConfirm.value !== props.detail.username) {
    emit('notify', '请输入完整账号名确认删除', 'error');
    return;
  }
  if (!window.confirm(`确定删除账号 ${props.detail.username}（UID ${props.detail.uid}）？账号与存档都会消失，不可恢复。`)) {
    return;
  }
  saving.value = 'delAccount';
  try {
    await api.deleteAccount(props.detail.uid);
    emit('notify', '账号已删除');
    emit('deleted');
  } catch (error) {
    emit('notify', error.message, 'error');
  } finally {
    saving.value = '';
  }
};
</script>

<template>
  <div>
    <div class="detail-head">
      <div class="avatar">{{ initial }}</div>
      <div class="detail-title">
        <h1>{{ acc.NickName || detail.username }}</h1>
        <div class="line">
          <span>UID {{ detail.uid }}</span>
          <span>账号 {{ detail.username }}</span>
          <span v-if="detail.isTest" class="badge test">预置测试账号</span>
          <span v-if="player">存档 {{ (player._blobSize / 1024).toFixed(1) }} KB</span>
          <span v-if="!player" class="badge empty">未建角色</span>
        </div>
      </div>
    </div>

    <div v-if="!player && section === 'accounts'" class="card">
      <div class="card-head"><h2>创建角色</h2><span class="hint">按测试模板生成，账号无需重登</span></div>
      <div class="warn">该账号还没有角色存档。可以直接在这里补建，也可以进游戏走 1004 建角流程。</div>
      <div class="grid-2" style="margin-top: 12px">
        <label class="field">
          <span>昵称（1-14 字，不能以数字开头）</span>
          <input v-model.trim="createForm.nickName" maxlength="14" />
        </label>
        <label class="field">
          <span>性别</span>
          <select v-model="createForm.male">
            <option :value="true">男</option>
            <option :value="false">女</option>
          </select>
        </label>
      </div>
      <div style="margin-top: 12px">
        <button class="btn primary" type="button" :disabled="saving === 'createChar'" @click="createCharacter">
          创建角色
        </button>
      </div>
    </div>

    <template v-if="player">
      <div v-show="section === 'accounts'" class="card">
        <div class="card-head">
          <h2>角色资料</h2>
          <span class="hint">对应 AccInfo，写回后客户端重登生效</span>
          <div class="actions">
            <button class="btn primary" type="button" :disabled="saving === 'profile'" @click="saveProfile">保存</button>
          </div>
        </div>
        <div class="grid-2">
          <label class="field">
            <span>昵称</span>
            <input v-model.trim="profile.nickName" maxlength="14" />
          </label>
          <label class="field">
            <span>签名（最多 60 字）</span>
            <input v-model="profile.signature" maxlength="60" />
          </label>
          <label class="field">
            <span>性别</span>
            <select v-model="profile.gender">
              <option :value="true">男</option>
              <option :value="false">女</option>
            </select>
          </label>
          <label class="field">
            <span>头像 ID</span>
            <input v-model.number="profile.headIcon" type="number" min="1" />
          </label>
          <label class="field">
            <span>个人页展示皮肤（如 10301，须已拥有）</span>
            <input v-model.number="profile.skinId" type="number" min="1" />
          </label>
        </div>
        <div class="note">
          角色：<code>{{ (acc.Chars || []).map((c) => `${c.CharId} Lv.${c.Level}`).join('、') || '无' }}</code>
          · 创建时间 {{ acc.CreateTime ? new Date(Number(acc.CreateTime) * 1000).toLocaleString() : '未知' }}
        </div>
      </div>

      <div v-show="section === 'data'" class="card">
        <div class="card-head">
          <h2>资源</h2>
          <span class="hint">PlayerInfo.Res，Tid 4 为星之彩</span>
          <div class="actions">
            <button class="btn" type="button" @click="addResource">加一行</button>
            <button class="btn primary" type="button" :disabled="saving === 'res'" @click="saveResources">保存</button>
          </div>
        </div>
        <table class="data">
          <thead>
            <tr><th>资源 ID</th><th>数量</th><th style="width: 60px"></th></tr>
          </thead>
          <tbody>
            <tr v-for="(item, index) in resources" :key="index">
              <td class="num"><input v-model.number="item.tid" type="number" min="1" /></td>
              <td class="num"><input v-model.number="item.qty" type="number" min="0" max="999999999" /></td>
              <td><button class="btn ghost" type="button" @click="resources.splice(index, 1)">删除</button></td>
            </tr>
            <tr v-if="!resources.length"><td colspan="3" class="empty-row">暂无资源条目</td></tr>
          </tbody>
        </table>
        <div class="note">保存会整体替换 Res 列表；Items、Chars、Formation 等其它字段原样保留。</div>
      </div>

      <div v-show="section === 'data'" class="card">
        <div class="card-head">
          <h2>引导进度</h2>
          <span class="hint">StepId = -1 表示该组已完成</span>
          <div class="actions">
            <button class="btn" type="button" @click="skipAllNewbies">1-3 组设为完成</button>
            <button class="btn primary" type="button" :disabled="saving === 'newbie'" @click="saveNewbies">保存</button>
          </div>
        </div>
        <table class="data">
          <thead>
            <tr><th>引导组</th><th>步骤</th><th style="width: 60px"></th></tr>
          </thead>
          <tbody>
            <tr v-for="(item, index) in newbies" :key="index">
              <td class="num"><input v-model.number="item.groupId" type="number" min="1" max="64" /></td>
              <td class="num"><input v-model.number="item.stepId" type="number" min="-1" max="10000" /></td>
              <td><button class="btn ghost" type="button" @click="newbies.splice(index, 1)">删除</button></td>
            </tr>
            <tr v-if="!newbies.length"><td colspan="3" class="empty-row">没有引导记录，游戏会按新手流程走</td></tr>
          </tbody>
        </table>
      </div>

      <div v-show="section === 'data'" class="card">
        <div class="card-head">
          <h2>大厅展示角色</h2>
          <span class="hint">PlayerInfo.Board，最多 8 个</span>
          <div class="actions">
            <button class="btn primary" type="button" :disabled="saving === 'board'" @click="saveBoard">保存</button>
          </div>
        </div>
        <div class="chips">
          <span v-for="(id, index) in board" :key="id" class="chip">
            {{ id }}
            <button type="button" @click="board.splice(index, 1)">×</button>
          </span>
          <span v-if="!board.length" class="empty-row">未设置展示角色</span>
        </div>
        <div class="inline-form">
          <input v-model="boardInput" type="number" min="1" placeholder="展示 ID，如 410301" @keyup.enter="addBoard" />
          <button class="btn" type="button" @click="addBoard">添加</button>
        </div>
      </div>

      <div v-show="section === 'data'" class="card">
        <div class="card-head"><h2>原始存档</h2><span class="hint">只读，便于核对字段</span>
          <div class="actions">
            <button class="btn ghost" type="button" @click="showRaw = !showRaw">
              {{ showRaw ? '收起' : '展开' }}
            </button>
          </div>
        </div>
        <pre v-if="showRaw" style="max-height: 320px; overflow: auto; margin: 0; font-size: 12px">{{ rawJson }}</pre>
        <div v-else class="note">共 {{ (acc.Chars || []).length }} 个角色、{{ (player.Res || []).length }} 条资源、{{ (player.Items || []).length }} 个道具。</div>
      </div>
    </template>

    <p v-if="!player && section === 'data'" class="warn">该账号尚无角色存档，请先到“账号管理”创建角色。</p>
    <div v-show="section === 'accounts'" class="card">
      <div class="card-head"><h2>账号操作</h2><span class="hint">直接改库，不走网关</span></div>
      <div class="grid-2">
        <div class="field">
          <span>重置密码（PBKDF2-SHA256 / 210000 轮，与网关一致）</span>
          <div class="inline-form" style="margin: 0">
            <input v-model="password" type="password" placeholder="新密码，8-128 位" maxlength="128" autocomplete="new-password" />
            <button class="btn" type="button" :disabled="Boolean(saving)" @click="resetPassword">{{ saving === 'pwd' ? '重置中…' : '重置密码' }}</button>
          </div>
        </div>
        <div class="field">
          <span>删除角色存档（保留账号，可重测建角）</span>
          <div class="inline-form" style="margin: 0">
            <button class="btn danger" type="button" :disabled="!player || saving === 'delPlayer'" @click="deletePlayer">
              删除存档
            </button>
          </div>
        </div>
      </div>
      <div v-if="!detail.isTest" style="margin-top: 16px">
        <div class="field">
          <span>删除账号（不可恢复，输入账号名确认）</span>
          <div class="inline-form" style="margin: 0">
            <input v-model="deleteConfirm" :placeholder="detail.username" />
            <button
              class="btn danger"
              type="button"
              :disabled="saving === 'delAccount' || deleteConfirm !== detail.username"
              @click="deleteAccount"
            >
              删除账号
            </button>
          </div>
        </div>
      </div>
      <div v-else class="note">预置测试账号受保护，不提供删除入口。</div>
    </div>
  </div>
</template>
