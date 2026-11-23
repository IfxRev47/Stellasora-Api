import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const children = [];

function start(name, command, args) {
  const child = spawn(command, args, {
    cwd: root,
    stdio: ['ignore', 'pipe', 'pipe'],
    shell: process.platform === 'win32',
    env: process.env,
  });
  const prefix = name.padEnd(4);
  child.stdout.on('data', (data) => process.stdout.write(`${prefix}| ${data}`));
  child.stderr.on('data', (data) => process.stderr.write(`${prefix}| ${data}`));
  child.on('exit', (code) => {
    if (code !== 0 && code !== null) {
      console.error(`[dev] ${name} 退出，code=${code}`);
      shutdown(code);
    }
  });
  children.push(child);
  return child;
}

function shutdown(code = 0) {
  for (const child of children) {
    if (!child.killed) child.kill();
  }
  process.exit(code);
}

process.on('SIGINT', () => shutdown(0));
process.on('SIGTERM', () => shutdown(0));

start('api', process.execPath, ['server/index.mjs']);
start('web', process.platform === 'win32' ? 'npx.cmd' : 'npx', ['vite']);
