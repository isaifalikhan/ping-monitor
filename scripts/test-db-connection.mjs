import postgres from 'postgres';

const sql = postgres('postgresql://postgres:postgres@127.0.0.1:5432/postgres');

try {
  const result = await sql`SELECT 1 as ok`;
  console.log('Connection OK:', result);
} catch (error) {
  console.error('Connection failed:', error.message);
  process.exit(1);
} finally {
  await sql.end();
}
