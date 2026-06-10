import postgres from 'postgres';

const sql = postgres(
  'postgresql://postgres:postgres@127.0.0.1:5432/postgres?sslmode=disable&pgbouncer=true',
);

await sql`UPDATE users SET email_verified = true WHERE email_verified = false`;
console.log('All users marked as email verified.');
await sql.end();
