export async function request(path, options = {}) {
  const response = await fetch(path, {
    signal: AbortSignal.timeout(15000),
    headers: options.body ? { 'Content-Type': 'application/json' } : undefined,
    ...options,
    body: options.body ? JSON.stringify(options.body) : undefined,
  });
  const text = await response.text();
  let data;
  try { data = text ? JSON.parse(text) : null; }
  catch { throw new Error(`接口未返回 JSON（HTTP ${response.status}），请检查 GM API 是否启动及 Vite 代理配置`); }
  if (!response.ok) {
    throw new Error(data?.error || `请求失败（HTTP ${response.status}）`);
  }
  return data;
}

export const api = {
  meta: () => request('/api/meta'),
  accounts: (query = '') => request(`/api/accounts?q=${encodeURIComponent(query)}`),
  account: (uid) => request(`/api/accounts/${uid}`),
  createAccount: (body) => request('/api/accounts', { method: 'POST', body }),
  setPassword: (uid, password) =>
    request(`/api/accounts/${uid}/password`, { method: 'PATCH', body: { password } }),
  deleteAccount: (uid) => request(`/api/accounts/${uid}`, { method: 'DELETE' }),
  patchProfile: (uid, body) => request(`/api/accounts/${uid}/profile`, { method: 'PATCH', body }),
  createPlayer: (uid, body) => request(`/api/accounts/${uid}/player`, { method: 'POST', body }),
  saveResources: (uid, items) => request(`/api/accounts/${uid}/resources`, { method: 'PUT', body: { items } }),
  saveNewbies: (uid, items) => request(`/api/accounts/${uid}/newbies`, { method: 'PUT', body: { items } }),
  saveBoard: (uid, items) => request(`/api/accounts/${uid}/board`, { method: 'PUT', body: { items } }),
  deletePlayer: (uid) => request(`/api/accounts/${uid}/player`, { method: 'DELETE' }),
};
