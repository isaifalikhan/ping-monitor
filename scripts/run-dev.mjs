import { spawn } from 'child_process';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const root = join(__dirname, '..');
const pnpm = process.platform === 'win32' ? 'pnpm.cmd' : 'pnpm';

function run(name, args, cwd = root) {
  const child = spawn(pnpm, args, {
    cwd,
    stdio: 'inherit',
    shell: true,
    env: process.env,
  });
  child.on('exit', (code) => {
    if (code !== 0 && code !== null) {
      console.error(`[${name}] exited with code ${code}`);
    }
  });
  return child;
}

console.log('Starting NetWatch development stack...\n');

const db = run('db', ['db:dev']);
const children = [db];

setTimeout(() => {
  run('backend', ['--filter', '@netwatch/backend', 'dev']);
  run('frontend', ['--filter', '@netwatch/frontend', 'dev', '--', '-p', '3001']);
}, 5000);

const shutdown = () => {
  children.forEach((c) => c.kill('SIGTERM'));
  process.exit(0);
};

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
