import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const gmRoot = path.resolve(here, '..');
export const repoRoot = path.resolve(gmRoot, '..');
export const gatewayRoot = path.resolve(repoRoot, 'server/Stella.LocalGateway');

/** 默认直接操作本地网关的 SQLite 库；GM_DB 可指向副本做演练。 */
export const dbPath = path.resolve(process.env.GM_DB || path.join(gatewayRoot, 'Data/stella.sqlite'));
export const protoDir = path.resolve(process.env.GM_PROTO_DIR || path.join(gatewayRoot, 'Protocol'));
export const seedDir = path.resolve(process.env.GM_SEED_DIR || path.join(gatewayRoot, 'Fixtures/test'));
export const port = Number(process.env.GM_PORT || 8787);
export const host = process.env.GM_HOST || '127.0.0.1';
export const distDir = path.join(gmRoot, 'dist');
