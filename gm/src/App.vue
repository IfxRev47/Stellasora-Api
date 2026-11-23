<script setup>
import { computed, onMounted, ref, watch } from 'vue';
import { api } from './api';
import AccountDetail from './components/AccountDetail.vue';
import RegistrationPanel from './components/RegistrationPanel.vue';
import ItemLookup from './components/ItemLookup.vue';
import PublicRegistration from './components/PublicRegistration.vue';
import AccountMail from './components/AccountMail.vue';
import NoticePanel from './components/NoticePanel.vue';
const publicRegistration = window.location.pathname === '/register';
const sections = [
  { id:'accounts', name:'账号管理', hint:'资料 · 密码 · 建号', icon:'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8 M20 8v6 M17 11h6' },
  { id:'data', name:'角色存档', hint:'资源 · 引导 · 展示', icon:'M4 4h16v16H4z M8 4v6h8V4 M8 16h8' },
  { id:'mail', name:'邮件发奖', hint:'附件 · 领取记录', icon:'M3 5h18v14H3z M3 5l9 7 9-7' },
  { id:'items', name:'物品目录', hint:'中文名 · ID 查询', icon:'M12 3l9 5-9 5-9-5 9-5z M3 8v9l9 5 9-5V8 M12 13v9' },
  { id:'registration', name:'注册管理', hint:'注册码 · 注册策略', icon:'M8 10V7a4 4 0 0 1 8 0v3 M5 10h14v11H5z M12 14v3' },
  { id:'notices', name:'公告管理', hint:'分类 · 发布 · 预览', icon:'M3 10v4h4l12 5V5L7 10H3z M7 14l2 7h4l-2-5' },
];
const initialTab = new URLSearchParams(window.location.search).get('tab');
const tab = ref(sections.some(x => x.id === initialTab) ? initialTab : 'accounts');
const currentSection = computed(() => sections.find(x => x.id === tab.value));
watch(tab, value => {
  const url = new URL(window.location.href); url.searchParams.set('tab',value);
  window.history.replaceState(null,'',url);
});

const meta = ref(null);
const accounts = ref([]);
const query = ref('');
const selectedUid = ref('');
const detail = ref(null);
const loadingList = ref(false);
const loadingDetail = ref(false);
const error = ref('');
const toasts = ref([]);
const showCreate = ref(false);
const creating = ref(false);
const form = ref({ username: '', password: '', nickname: '', male: true, registrationCode: '' });

const selected = computed(() => accounts.value.find((item) => item.uid === selectedUid.value) ?? null);

function toast(message, kind = 'ok') {
  const id = Math.random().toString(36).slice(2);
  toasts.value.push({ id, message, kind });
  setTimeout(() => {
    toasts.value = toasts.value.filter((item) => item.id !== id);
  }, 3200);
}

async function loadMeta() {
  try {
    meta.value = await api.meta();
  } catch (err) {
    error.value = err.message;
  }
}

async function loadAccounts({ keep = false } = {}) {
  loadingList.value = true;
  try {
    const data = await api.accounts(query.value);
    accounts.value = data.accounts;
    error.value = '';
    if (!keep) {
      const stillThere = data.accounts.some((item) => item.uid === selectedUid.value);
      if (!stillThere) selectedUid.value = data.accounts[0]?.uid ?? '';
    }
  } catch (err) {
    error.value = err.message;
    accounts.value = [];
  } finally {
    loadingList.value = false;
  }
}

async function loadDetail(uid) {
  if (!uid) {
    detail.value = null;
    return;
  }
  loadingDetail.value = true;
  try {
    detail.value = await api.account(uid);
  } catch (err) {
    toast(err.message, 'error');
    detail.value = null;
  } finally {
    loadingDetail.value = false;
  }
}

function selectAccount(uid) {
  selectedUid.value = uid;
}

async function refreshAll(message) {
  await Promise.all([loadAccounts({ keep: true }), loadDetail(selectedUid.value), loadMeta()]);
  if (message) toast(message);
}

async function handleDeleted() {
  detail.value = null;
  selectedUid.value = '';
  await loadAccounts();
  await loadMeta();
}

async function submitCreate() {
  if (creating.value) return;
  creating.value = true;
  try {
    const created = await api.createAccount({ ...form.value });
    showCreate.value = false;
    form.value = { username: '', password: '', nickname: '', male: true, registrationCode: '' };
    await loadAccounts({ keep: true });
    selectedUid.value = created.uid;
    await Promise.all([loadDetail(created.uid), loadMeta()]);
    toast(`已创建 UID ${created.uid}（${created.hasCharacter ? '含角色存档' : '未建角色'}）`);
  } catch (err) {
    toast(err.message, 'error');
  } finally {
    creating.value = false;
  }
}

let timer = 0;
watch(query, () => {
  clearTimeout(timer);
  timer = setTimeout(() => loadAccounts(), 250);
});

watch(selectedUid, (uid) => loadDetail(uid));

onMounted(async () => {
  if (publicRegistration) return;
  await Promise.all([loadMeta(), loadAccounts()]);
});
</script>

<template>
  <PublicRegistration v-if="publicRegistration" />
  <div v-else class="app">
    <header class="topbar">
      <div class="brand">星塔旅人 · 本地 GM <span>{{ currentSection.name }}</span></div>
      <div class="topbar-meta">
        <span><i class="dot"></i>SQLite 直连</span>
        <span v-if="meta">账号 <b>{{ meta.accounts }}</b> · 存档 <b>{{ meta.players }}</b></span>
        <span v-if="meta" :title="meta.database">schema v{{ meta.schemaVersion }}</span>
        <button class="btn ghost" type="button" @click="refreshAll()">刷新</button>
      </div>
    </header>

    <div v-if="tab === 'notices'" class="detail page-main"><NoticePanel @notify="toast" /></div>
    <div v-else-if="tab === 'registration'" class="detail page-main"><div class="section-heading"><div><p class="eyebrow">账号服务</p><h1>注册管理</h1><p>管理注册码、注册策略与玩家注册入口。</p></div></div><RegistrationPanel @notify="toast" /></div>
    <div v-else-if="tab === 'items'" class="detail page-main"><div class="section-heading"><div><p class="eyebrow">内容查询</p><h1>物品目录</h1><p>查询物品后，前往“邮件发奖”选择附件并发放。</p></div><button class="btn" @click="tab='mail'">前往邮件发奖</button></div><section class="card"><ItemLookup @pick="tab='mail'; toast('请在邮件下发区添加附件')" /></section></div>
    <div v-else class="workspace">
      <aside class="sidebar">
        <div class="sidebar-head">
          <div class="sidebar-actions">
            <button class="btn primary" type="button" @click="showCreate = true">新建账号</button>
            <button class="btn" type="button" :disabled="loadingList" @click="loadAccounts()">
              {{ loadingList ? '读取中' : '重新载入' }}
            </button>
          </div>
          <input v-model="query" class="search" type="search" placeholder="搜索 UID / 账号" />
        </div>

        <div class="account-list">
          <div v-if="error" class="empty-row">{{ error }}</div>
          <div v-else-if="!accounts.length" class="empty-row">没有匹配的账号</div>
          <button
            v-for="item in accounts"
            :key="item.uid"
            type="button"
            class="account-item"
            :class="{ active: item.uid === selectedUid }"
            @click="selectAccount(item.uid)"
          >
            <div class="row">
              <span class="nick">{{ item.nickname || item.username }}</span>
              <span v-if="item.isTest" class="badge test">测试</span>
              <span v-if="!item.hasCharacter" class="badge empty">未建角</span>
            </div>
            <div class="sub">
              <span>UID {{ item.uid }}</span>
              <span>{{ item.username }}</span>
              <span v-if="item.hasCharacter">Lv.{{ item.level }}</span>
            </div>
          </button>
        </div>
      </aside>

      <main class="detail">
        <div class="section-heading"><div><p class="eyebrow">{{ currentSection.hint }}</p><h1>{{ currentSection.name }}</h1><p v-if="detail">当前账号 {{ detail.username }} · UID {{ detail.uid }}</p></div></div>
        <div v-if="loadingDetail" class="placeholder"><h2>读取存档…</h2></div>
        <template v-else-if="tab === 'mail' && detail">
          <AccountMail v-if="detail.player" :key="detail.uid" :uid="detail.uid" @notify="toast" />
          <div v-else class="warn">该账号尚无角色，请先到“账号管理”创建角色再发送邮件。</div>
        </template>
        <AccountDetail
          v-else-if="detail"
          :key="detail.uid"
          :detail="detail"
          :section="tab"
          @saved="refreshAll"
          @notify="toast"
          @deleted="handleDeleted"
        />
        <div v-else class="placeholder">
          <h2>选择左侧账号</h2>
          <div>修改资料、资源、引导进度和展示角色</div>
        </div>
      </main>
    </div>

    <nav class="bottom-nav" aria-label="功能分类导航">
      <button v-for="section in sections" :key="section.id" type="button" :class="{active:tab === section.id}"
        :aria-current="tab === section.id ? 'page' : undefined" :title="section.hint" @click="tab=section.id">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path :d="section.icon" /></svg>
        <span>{{ section.name }}</span><small>{{ section.hint }}</small>
      </button>
    </nav>

    <div v-if="showCreate" class="mask" @click.self="showCreate = false">
      <form class="dialog" @submit.prevent="submitCreate">
        <h2>新建本地账号</h2>
        <p class="sub">账号写入 SQLite 后可直接在游戏里登录；填了昵称会同时按测试模板建好角色。</p>
        <div class="fields">
          <label class="field">
            <span>账号（3-32 位字母、数字、下划线或短横线）</span>
            <input v-model.trim="form.username" required autocomplete="off" />
          </label>
          <label class="field">
            <span>密码（8-128 位）</span>
            <input v-model="form.password" type="password" required autocomplete="new-password" />
          </label>
          <label class="field">
            <span>角色昵称（留空则只建账号）</span>
            <input v-model.trim="form.nickname" maxlength="14" placeholder="1-14 字，不能以数字开头" />
          </label>
          <label class="field"><span>注册码（管理员建号可留空；填入后会校验并扣除次数）</span><input v-model.trim="form.registrationCode" maxlength="80" /></label>
          <label class="field">
            <span>性别</span>
            <select v-model="form.male">
              <option :value="true">男</option>
              <option :value="false">女</option>
            </select>
          </label>
        </div>
        <div class="dialog-foot">
          <button class="btn" type="button" @click="showCreate = false">取消</button>
          <button class="btn primary" type="submit" :disabled="creating">
            {{ creating ? '创建中…' : '创建' }}
          </button>
        </div>
      </form>
    </div>

    <div class="toasts">
      <div v-for="item in toasts" :key="item.id" class="toast" :class="{ error: item.kind === 'error' }">
        {{ item.message }}
      </div>
    </div>
  </div>
</template>
