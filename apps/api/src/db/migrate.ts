import 'dotenv/config';
import { readFile, readdir } from 'node:fs/promises';
import path, { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { pool } from './pool.js';

async function run() {
  const currentDir = dirname(fileURLToPath(import.meta.url));
  const repoRoot = path.resolve(currentDir, '../../../../');
  const migrationsDir = path.resolve(repoRoot, 'infra/migrations');
  const files = (await readdir(migrationsDir)).filter((file) => file.endsWith('.sql')).sort();

  const client = await pool.connect();
  try {
    await client.query('BEGIN');
    await client.query(`
      CREATE TABLE IF NOT EXISTS schema_migrations (
        id TEXT PRIMARY KEY,
        applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
      )
    `);

    for (const file of files) {
      const already = await client.query('SELECT 1 FROM schema_migrations WHERE id = $1', [file]);
      if (already.rowCount) continue;

      const sql = await readFile(path.join(migrationsDir, file), 'utf-8');
      await client.query(sql);
      await client.query('INSERT INTO schema_migrations(id) VALUES ($1)', [file]);
      // eslint-disable-next-line no-console
      console.log(`Applied migration: ${file}`);
    }

    await client.query('COMMIT');
  } catch (error) {
    await client.query('ROLLBACK');
    throw error;
  } finally {
    client.release();
    await pool.end();
  }
}

run().catch((error) => {
  // eslint-disable-next-line no-console
  console.error(error);
  process.exit(1);
});
