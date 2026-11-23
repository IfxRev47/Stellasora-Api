<script setup>
import { onMounted, reactive, ref } from 'vue';
import { request } from '../api';
const form = reactive({ username: '', password: '', registrationCode: '' });
const confirmPassword = ref('');
const requireCode = ref(false);
const busy = ref(false);
const error = ref('');
const success = ref('');
onMounted(async () => {
  try { requireCode.value = (await request('/api/registration/settings')).requireCode; }
  catch (err) { error.value = err.message; }
});
async function submit() {
  if (busy.value) return;
  error.value = ''; success.value = '';
  if (form.password !== confirmPassword.value) { error.value = '两次密码不一致'; return; }
  busy.value = true;
  try {
    const result = await request('/api/register', { method: 'POST', body: form });
    success.value = `账号 ${result.username} 注册成功，UID ${result.uid}。请使用该账号密码登录游戏并创建角色。`;
    form.password = ''; confirmPassword.value = ''; form.registrationCode = '';
  } catch (err) { error.value = err.message; }
  finally { busy.value = false; }
}
</script>
<template>
  <main style="max-width:520px;margin:60px auto;padding:20px">
    <section class="card"><h1>星塔旅人 · 账号注册</h1><p>注册成功后在游戏内创建角色。</p>
      <form @submit.prevent="submit">
        <label class="field"><span>账号</span><input v-model.trim="form.username" required pattern="[A-Za-z0-9_-]{3,32}" placeholder="3-32 位字母、数字、下划线或短横线" autocomplete="username" /></label>
        <label class="field"><span>密码</span><input v-model="form.password" required type="password" minlength="8" maxlength="128" autocomplete="new-password" /></label>
        <label class="field"><span>确认密码</span><input v-model="confirmPassword" required type="password" minlength="8" maxlength="128" autocomplete="new-password" /></label>
        <label class="field"><span>注册码（{{ requireCode ? '必填' : '选填' }}）</span><input v-model.trim="form.registrationCode" :required="requireCode" maxlength="80" autocomplete="off" /></label>
        <p v-if="error" class="warn" role="alert">{{ error }}</p><p v-if="success" role="status">{{ success }}</p>
        <button class="btn primary" :disabled="busy">{{ busy ? '注册中…' : '注册账号' }}</button>
      </form>
    </section>
  </main>
</template>
