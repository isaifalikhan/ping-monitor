import { PGlite } from '@electric-sql/pglite';
import { PGLiteSocketServer } from '@electric-sql/pglite-socket';
import { mkdir } from 'fs/promises';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const dataDir = join(__dirname, '..', '.pglite');

await mkdir(dataDir, { recursive: true });

const db = new PGlite(dataDir);
const server = new PGLiteSocketServer({
  db,
  port: 5432,
  host: '127.0.0.1',
  maxConnections: 20,
});
await server.start();

console.log('PGlite socket server running at localhost:5432');
console.log('Press Ctrl+C to stop.');

const shutdown = async () => {
  await server.stop();
  await db.close();
  process.exit(0);
};

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
