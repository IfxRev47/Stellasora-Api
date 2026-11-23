<script setup>
import { computed, onMounted, reactive, ref } from 'vue';
import { request } from '../api';
const emit = defineEmits(['notify']);
const notices = ref([]);
const busy = ref(false);
const error = ref('');
const selected = ref(null);
const categories = { system: '系统公告', activity: '活动公告', else_1: '其他公告一', else_2: '其他公告二' };
const form = reactive({ category:'system', title:'', content:'', enabled:true, start:'', end:'', sort:100 });
const permanent = computed(() => selected.value === 1 || selected.value === 2);
function localTime(seconds) {
  if (!seconds) return '';
  const date = new Date(seconds*1000);
  return new Date(date.getTime()-date.getTimezoneOffset()*60000).toISOString().slice(0,16);
}
function edit(notice) {
  selected.value = notice?.id ?? null;
  Object.assign(form, notice ? { category:notice.category,title:notice.title,content:notice.content,enabled:!!notice.enabled,
    start:localTime(notice.start_at),end:localTime(notice.end_at),sort:notice.sort } :
    { category:'system',title:'',content:'',enabled:true,start:'',end:'',sort:100 });
  error.value = '';
}
async function load() {
  notices.value = (await request('/api/notices')).notices;
}
async function save() {
  if (busy.value) return;
  busy.value = true;
  try {
    const body = { ...form, startAt:form.start ? Math.floor(new Date(form.start).getTime()/1000) : 0,
      endAt:form.end ? Math.floor(new Date(form.end).getTime()/1000) : 0 };
    const result = await request(selected.value ? `/api/notices/${selected.value}` : '/api/notices', {method:selected.value ? 'PUT' : 'POST',body});
    await load(); edit(notices.value.find(x => x.id === result.id));
    emit('notify','公告已保存，客户端重新打开公告获取更新');
  } catch (err) { error.value = err.message; emit('notify',err.message,'error'); }
  finally { busy.value = false; }
}
async function remove(notice) {
  if (!window.confirm(`删除公告“${notice.title}”？`)) return;
  busy.value = true;
  try {
    await request(`/api/notices/${notice.id}`,{method:'DELETE'}); await load();
    if (selected.value === notice.id) edit(null);
    emit('notify','公告已删除');
  } catch (err) { error.value = err.message; }
  finally { busy.value = false; }
}
function status(notice) {
  const now = Date.now()/1000;
  return !notice.enabled ? '草稿' : notice.start_at > now ? '待发布' : notice.end_at && notice.end_at <= now ? '已结束' : '展示中';
}
onMounted(async () => { try { await load(); if (notices.value.length) edit(notices.value[0]); } catch (err) { error.value = err.message; } });
</script>
<template>
  <div class="section-heading"><div><p class="eyebrow">运营管理</p><h1>公告管理</h1><p>按分类发布公告，正文在游戏公告面板中展示。</p></div><button class="btn primary" :disabled="busy" @click="edit(null)">新建公告</button></div>
  <div class="notice-layout">
    <section class="card notice-list"><div class="card-head"><h2>公告列表</h2></div>
      <button v-for="notice in notices" :key="notice.id" class="account-item" :class="{active:notice.id === selected}" :disabled="busy" @click="edit(notice)">
        <div class="row"><span class="nick">{{ notice.title }}</span><span class="badge">{{ status(notice) }}</span></div>
        <div class="sub">{{ categories[notice.category] }} · #{{ notice.id }}{{ notice.id <= 2 ? ' · 常驻' : '' }}</div>
      </button>
    </section>
    <div>
      <section class="card"><div class="card-head"><h2>{{ selected ? '编辑公告' : '新建公告' }}</h2></div>
        <p v-if="permanent" class="note">常驻公告保证客户端至少有两个可见分类，可编辑标题、正文与排序。</p>
        <form class="stack-form" @submit.prevent="save">
          <div class="grid-2">
            <label class="field"><span>分类</span><select v-model="form.category" :disabled="permanent || busy"><option v-for="(name,key) in categories" :key="key" :value="key">{{ name }}</option></select></label>
            <label class="field"><span>排序（越大越靠前）</span><input v-model.number="form.sort" required type="number" min="0" max="999999" /></label>
          </div>
          <label class="field"><span>标题</span><input v-model="form.title" required maxlength="100" /></label>
          <label class="field"><span>正文（纯文本，保留换行）</span><textarea v-model="form.content" required maxlength="20000" rows="10"></textarea></label>
          <div class="grid-2">
            <label class="field"><span>开始时间（留空立即生效）</span><input v-model="form.start" type="datetime-local" :disabled="permanent" /></label>
            <label class="field"><span>结束时间（留空长期有效）</span><input v-model="form.end" type="datetime-local" :disabled="permanent" /></label>
          </div>
          <label><input v-model="form.enabled" type="checkbox" :disabled="permanent" /> 发布到游戏</label>
          <p v-if="error" class="warn" role="alert">{{ error }}</p>
          <div class="actions"><button class="btn primary" :disabled="busy">{{ busy ? '保存中…' : '保存公告' }}</button>
            <button v-if="selected && !permanent" type="button" class="btn danger" :disabled="busy" @click="remove({id:selected,title:form.title})">删除公告</button></div>
        </form>
      </section>
      <section class="card"><div class="card-head"><h2>正文预览</h2></div><article class="notice-preview"><h2>{{ form.title || '公告标题' }}</h2><div>{{ form.content || '在上方填写公告正文' }}</div></article></section>
    </div>
  </div>
</template>
