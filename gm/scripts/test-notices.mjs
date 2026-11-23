// End-to-end test against temporary SQLite and independent gateway/GM processes.
// Build first: dotnet build server/Stella.LocalGateway -o work/notice-check -p:UseAppHost=false
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import protobuf from 'protobufjs';
import { gatewayRoot, gmRoot, repoRoot } from '../server/config.mjs';

const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'stella-notice-'));
const database = path.join(directory, 'test.sqlite');
const dll = process.env.NOTICE_GATEWAY_DLL || path.join(repoRoot,'work/notice-check/Stella.LocalGateway.dll');
const key = Buffer.from('notice-test-key1');
const children = [];
let logs = '';
function start(command, args, cwd, env) {
  const child = spawn(command, args, { cwd, env:{...process.env,...env}, windowsHide:true, stdio:['ignore','pipe','pipe'] });
  child.stdout.on('data', data => { logs += data; });
  child.stderr.on('data', data => { logs += data; });
  children.push(child);
}
async function ready(url) {
  for (let i=0;i<100;i++) {
    try { if ((await fetch(url)).ok) return; } catch {}
    await new Promise(resolve => setTimeout(resolve,100));
  }
  throw new Error(`Startup failed: ${url}\n${logs}`);
}
const gm='http://127.0.0.1:8789';
const gateway='http://127.0.0.1:1991';
async function api(route, method='GET', body, status=200) {
  const response=await fetch(gm+'/api'+route,{method,headers:body ? {'Content-Type':'application/json'} : {},body:body ? JSON.stringify(body) : undefined});
  const json=await response.json();
  assert.equal(response.status,status,JSON.stringify(json));
  return json;
}
function decrypt(bytes) {
  const iv=bytes.subarray(0,16);
  const cipher=crypto.createDecipheriv('aes-128-cbc',key,iv);
  return Buffer.concat([cipher.update(bytes.subarray(16)),cipher.final()]);
}
const root = new protobuf.Root().loadSync(path.join(gatewayRoot,'Protocol/meta.proto'),{keepCase:true});
const NoticeList=root.lookupType('pb.NoticeList');
async function listing() {
  const response=await fetch(gateway+'/notice/noticelist.html');
  assert.equal(response.status,200);
  assert.equal(response.headers.get('cache-control'),'no-store');
  return NoticeList.decode(decrypt(Buffer.from(await response.arrayBuffer())));
}
try {
  start('dotnet',[dll],gatewayRoot,{Stella__Port:'1991',Stella__ListenAddress:'127.0.0.1',Stella__DatabasePath:database,Stella__ServerMetaKey:key.toString(),Stella__TraceProtocol:'false'});
  await ready(gateway+'/healthz');
  start(process.execPath,['server/index.mjs'],gmRoot,{GM_DB:database,GM_PORT:'8789'});
  await ready(gm+'/api/meta');
  let list=await listing();
  assert.equal(list.List.length,4);
  assert(list.List.find(x=>x.Name==='system').Detail.length>0);
  assert(list.List.find(x=>x.Name==='activity').Detail.length>0);
  for (const category of list.List) for (const item of category.Detail) {
    assert(item.Channel.includes(1));
    assert(item.Channel.includes(4));
    assert(Number(item.EndTime)>Date.now()/1000);
    assert(item.ContentUrl.startsWith(gateway+'/notice/content/'));
    const response=await fetch(item.ContentUrl);
    assert.equal(response.status,200);
    assert(decrypt(Buffer.from(await response.arrayBuffer())).toString().includes('<article'));
  }
  console.log('PASS encrypted list/content, channels, two populated categories, absolute URLs');
  const body={category:'system',title:'标题 <script>',content:'第一行\n<script>alert(1)</script> & "正文"',enabled:true,startAt:0,endAt:0,sort:999};
  const created=await api('/notices','POST',body,201);
  list=await listing();
  const item=list.List.find(x=>x.Name==='system').Detail[0];
  assert.equal(Number(item.Id),created.id);
  let response=await fetch(item.ContentUrl);
  const html=decrypt(Buffer.from(await response.arrayBuffer())).toString();
  assert(html.includes('第一行\n&lt;script&gt;'));
  assert(!html.includes('<script>'));
  console.log('PASS GM create, sort, UTF-8, escaped HTML, preserved line breaks');
  await api('/notices/'+created.id,'PUT',{...body,enabled:false});
  assert(!(await listing()).List.flatMap(x=>x.Detail).some(x=>Number(x.Id)===created.id));
  assert.equal((await fetch(item.ContentUrl)).status,404);
  await api('/notices/'+created.id,'PUT',{...body,startAt:Math.floor(Date.now()/1000)+3600});
  assert(!(await listing()).List.flatMap(x=>x.Detail).some(x=>Number(x.Id)===created.id));
  await api('/notices/'+created.id,'PUT',{...body,endAt:1});
  assert(!(await listing()).List.flatMap(x=>x.Detail).some(x=>Number(x.Id)===created.id));
  console.log('PASS unpublished/future/expired notices excluded and content unavailable');
  await api('/notices/1','DELETE',undefined,400);
  await api('/notices/1','PUT',{...body,enabled:false},400);
  await api('/notices','POST',{...body,category:'bad'},400);
  await api('/notices','POST',{...body,startAt:100,endAt:50},400);
  await api('/notices/'+created.id,'DELETE');
  assert(!(await api('/notices')).notices.some(x=>x.id===created.id));
  console.log('PASS permanent categories protected, validation, deletion');
  console.log('All announcement integration checks passed.');
} catch (error) {
  console.error(error);
  console.error(logs.slice(-5000));
  process.exitCode=1;
} finally {
  for (const child of children) child.kill();
}
