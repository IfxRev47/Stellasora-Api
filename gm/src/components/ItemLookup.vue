<script setup>
import { onMounted, ref, watch } from 'vue';
import { request } from '../api';
const props = defineProps({ awardable: Boolean });
const emit = defineEmits(['pick']);
const query = ref('');
const items = ref([]);
const total = ref(0);
const error = ref('');
const loading = ref(false);
let sequence = 0;
let timer;
async function search() {
  const current = ++sequence;
  loading.value = true;
  try {
    const result = await request(`/api/catalog/items?q=${encodeURIComponent(query.value)}${props.awardable ? '&awardable=1' : ''}`);
    if (current !== sequence) return;
    items.value = result.items; total.value = result.total; error.value = '';
  } catch (err) { if (current === sequence) error.value = err.message; }
  finally { if (current === sequence) loading.value = false; }
}
watch(query, () => { clearTimeout(timer); timer = setTimeout(search, 250); });
onMounted(search);
</script>
<template>
  <div>
    <input v-model="query" class="search" type="search" placeholder="输入物品 ID 或中文名称查询" />
    <div class="note">{{ loading ? '查询中…' : `共 ${total} 个结果，最多展示 100 条` }} · 发放支持资源与普通背包道具</div>
    <div v-if="error" class="warn">{{ error }}</div>
    <div style="max-height: 340px; overflow: auto">
      <table class="data">
        <thead><tr><th>ID</th><th>名称 / 说明</th><th>类型</th><th></th></tr></thead>
        <tbody>
          <tr v-for="item in items" :key="item.id">
            <td>{{ item.id }}</td><td>{{ item.name }}<div class="hint">{{ item.description }}</div></td>
            <td>{{ item.type === 1 ? '资源' : item.type === 2 ? '道具' : `类型 ${item.type}` }}</td>
            <td><button class="btn" :disabled="!item.awardable" @click="emit('pick', item)">{{ item.awardable ? '选为附件' : '暂不支持发放' }}</button></td>
          </tr>
          <tr v-if="!items.length"><td colspan="4">没有匹配结果</td></tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
