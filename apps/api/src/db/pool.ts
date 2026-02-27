import { Pool } from 'pg';

const connectionString = process.env.DATABASE_URL ?? 'postgres://printo:printo@localhost:5432/printo';

export const pool = new Pool({ connectionString });
