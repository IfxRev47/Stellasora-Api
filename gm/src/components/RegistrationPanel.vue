<script setup>
import { onMounted, reactive, ref } from 'vue';
import { request } from '../api';
const emit = defineEmits(['notify']);
const codes = ref([]);
const requireCode = ref(false);
const busy = ref(false);
const error = ref('');
const history = ref(null);
const generated = ref('');
const form = reactive({ count: 1, maxUses: 1, days: 7, note: '' });
async function load() {
  const [settings, list] = await Promise.all([request('/api/registration/settings'), request('/api/registration/codes')]);
  requireCode.value = settings.requireCode; codes.value = list.codes;
}
async function run(action, message) {
  if (busy.value) return;
  busy.value = true;
  try { await action(); await load(); error.value = ''; if (message) emit('notify', message); }
  catch (err) { error.value = err.message; emit('notify', err.message, 'error'); }
  finally { busy.value = false; }
}
function create() {
  run(async () => { generated.value = (await request('/api/registration/codes', { method: 'POST', body: form })).codes.join('\n'); }, '注册码已生成');
}
function toggle(code) {
  run(() => request(`/api/registration/codes/${code.code}`, { method: 'PATCH', body: { disabled: !code.disabled } }), code.disabled ? '已启用' : '已停用');
}
function inspect(code) {
  run(async () => { history.value = { code, rows: (await request(`/api/registration/codes/${code}/history`)).history }; });
}
function status(code) {
  return code.disabled ? '已停用' : code.expires_at && code.expires_at <= Date.now()/1000 ? '已过期' : code.uses >= code.max_uses ? '已用完' : '可用';
}
onMounted(() => run(async () => {}));
</script>
<template>
  <section class="card">
    <div class="card-head"><h2>注册与注册码</h2><button class="btn" :disabled="busy" @click="run(async () => {})">刷新</button></div>
    <p>玩家注册入口：<a href="/register" target="_blank" rel="noopener">打开注册页</a>。注册只创建账号，角色在游戏内创建。</p>
    <label class="field"><span>玩家注册策略（GM 管理员建号可不填注册码）</span>
      <select v-model="requireCode" :disabled="busy" @change="run(() => request('/api/registration/settings', {method:'PUT',body:{requireCode}}), '注册策略已保存')">
        <option :value="false">开放注册</option><option :value="true">必须使用注册码</option>
      </select>
    </label>
    <form @submit.prevent="create">
      <div class="grid-2">
        <label class="field"><span>生成数量（1-100）</span><input v-model.number="form.count" required type="number" min="1" max="100" /></label>
        <label class="field"><span>每码可用次数</span><input v-model.number="form.maxUses" required type="number" min="1" max="10000" /></label>
        <label class="field"><span>有效天数</span><input v-model.number="form.days" required type="number" min="1" max="365" /></label>
        <label class="field"><span>备注</span><input v-model="form.note" maxlength="200" /></label>
      </div>
      <button class="btn primary" :disabled="busy">{{ busy ? '处理中…' : '批量生成注册码' }}</button>
    </form>
    <label v-if="generated" class="field"><span>本批生成结果（可选中复制）</span><textarea readonly :value="generated" rows="4"></textarea></label>
    <p v-if="error" class="warn">{{ error }}</p>
    <div style="overflow:auto; max-height:500px">
      <table class="data"><thead><tr><th>注册码 / 备注</th><th>使用</th><th>截止时间</th><th>状态</th><th>操作</th></tr></thead>
        <tbody><tr v-for="code in codes" :key="code.code"><td><code>{{ code.code }}</code><div class="hint">{{ code.note }}</div></td>
          <td>{{ code.uses }} / {{ code.max_uses }}</td><td>{{ code.expires_at ? new Date(code.expires_at*1000).toLocaleString() : '永久' }}</td>
          <td>{{ status(code) }}</td><td><button class="btn" :disabled="busy" @click="toggle(code)">{{ code.disabled ? '启用' : '停用' }}</button>
          <button class="btn ghost" :disabled="busy" @click="inspect(code.code)">记录</button></td></tr>
          <tr v-if="!codes.length"><td colspan="5">暂无注册码</td></tr></tbody>
      </table>
    </div>
    <div v-if="history"><p>{{ history.code }} 使用记录</p><div v-for="row in history.rows" :key="row.uid">UID {{ row.uid }} · {{ row.username }} · {{ new Date(row.used_at*1000).toLocaleString() }}</div><p v-if="!history.rows.length">尚未使用</p></div>
  </section>
</template>
