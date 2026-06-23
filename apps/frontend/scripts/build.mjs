process.env.NODE_OPTIONS = '--max-old-space-size=8192';
import { spawnSync } from 'child_process';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const nextBin = join(__dirname, '../node_modules/next/dist/bin/next');

const result = spawnSync(process.execPath, [nextBin, 'build'], {
  stdio: 'inherit',
  env: process.env,
  cwd: join(__dirname, '..'),
});

process.exit(result.status ?? 1);
