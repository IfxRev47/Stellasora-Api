<script setup>
import { onMounted, reactive, ref } from 'vue';
import { request } from '../api';
import ItemLookup from './ItemLookup.vue';
const props = defineProps({ uid: { type: String, required: true } });
const emit = defineEmits(['notify']);
const mails = ref([]);
const busy = ref(false);
const lookup = ref(false);
const error = ref('');
const form = reactive({ subject: '', desc: '', author: '本地 GM', days: 30, attachments: [] });
async function load() {
  try { mails.value = (await request(`/api/accounts/${props.uid}/mails`)).mails; error.value = ''; }
  catch (err) { error.value = err.message; }
}
function pick(item) {
  const existing = form.attachments.find(x => x.tid === item.id);
  if (existing) existing.qty += 1;
  else form.attachments.push({ tid: item.id, name: item.name, qty: 1 });
}
async function send() {
  if (busy.value) return;
  busy.value = true;
  try {
    const result = await request(`/api/accounts/${props.uid}/mails`, { method: 'POST', body: form });
    emit('notify', `邮件 ${result.id} 已下发，打开游戏邮箱刷新可见`);
    form.subject = ''; form.desc = ''; form.attachments = []; error.value = '';
    await load();
  } catch (err) { error.value = err.message; emit('notify', err.message, 'error'); }
  finally { busy.value = false; }
}
onMounted(load);
</script>
<template>
  <section class="card">
    <div class="card-head"><h2>邮件下发与物品查询</h2><span class="hint">当前 UID {{ uid }}</span></div>
    <form @submit.prevent="send">
      <div class="grid-2">
        <label class="field"><span>邮件标题</span><input v-model="form.subject" required maxlength="100" /></label>
        <label class="field"><span>发件人</span><input v-model="form.author" required maxlength="60" /></label>
        <label class="field"><span>有效天数（1-365）</span><input v-model.number="form.days" type="number" min="1" max="365" required /></label>
      </div>
      <label class="field"><span>邮件内容</span><textarea v-model="form.desc" required maxlength="4000" rows="3"></textarea></label>
      <button class="btn" type="button" @click="lookup = !lookup">{{ lookup ? '收起物品查询' : '查询物品并添加附件' }}</button>
      <ItemLookup v-if="lookup" awardable @pick="pick" />
      <table v-if="form.attachments.length" class="data">
        <thead><tr><th>附件 ID / 名称</th><th>数量</th><th></th></tr></thead>
        <tbody><tr v-for="(item, i) in form.attachments" :key="item.tid"><td>{{ item.tid }} · {{ item.name }}</td>
          <td><input v-model.number="item.qty" type="number" min="1" max="999999999" required /></td>
          <td><button class="btn ghost" type="button" @click="form.attachments.splice(i,1)">移除</button></td></tr></tbody>
      </table>
      <p v-if="error" class="warn">{{ error }}</p>
      <p class="note">附件由游戏内领取后入库；重复领取不会重复发奖。角色、装备、皮肤类物品暂不支持。</p>
      <button class="btn primary" type="submit" :disabled="busy">{{ busy ? '下发中…' : '下发给当前角色' }}</button>
    </form>
    <div class="card-head" style="margin-top: 20px"><h2>已下发邮件</h2><button class="btn" @click="load">刷新</button></div>
    <table class="data"><thead><tr><th>ID / 标题</th><th>附件</th><th>状态</th><th>截止时间</th></tr></thead>
      <tbody><tr v-for="mail in mails" :key="mail.Id"><td>{{ mail.Id }} · {{ mail.Subject }}</td>
        <td>{{ mail.Attachments.map(x => `${x.Tid} × ${x.Qty}`).join('、') || '无' }}</td>
        <td>{{ mail.Recv ? '已领取' : mail.Read ? '已读' : '未读' }}{{ mail.Pin ? ' · 置顶' : '' }}</td>
        <td>{{ new Date(Number(mail.Deadline)*1000).toLocaleString() }}</td></tr>
        <tr v-if="!mails.length"><td colspan="4">暂无邮件</td></tr></tbody>
    </table>
  </section>
</template>
