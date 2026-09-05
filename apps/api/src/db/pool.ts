import { Pool } from 'pg';

const connectionString = process.env.DATABASE_URL ?? 'postgres://printo:printo@localhost:5432/printo';

/**
 * Fails early, and legibly, on a connection string that is not a URL.
 *
 * `pg` builds its client lazily, so a malformed `DATABASE_URL` surfaces as a bare
 * `TypeError: Invalid URL` from deep inside `pg-connection-string`, with the string itself
 * redacted from the message. The overwhelmingly common cause is a generated password
 * containing `/`, `@`, `+` or `:` - `openssl rand -base64` produces all four - dropped
 * unencoded into the userinfo part of the URL. Saying so is the difference between a
 * two-minute fix and an afternoon.
 */
function assertUsable(value: string): void {
  try {
    new URL(value);
  } catch {
    throw new Error(
      'DATABASE_URL is not a valid URL. If the password contains any of / @ : + # ? it must be ' +
        'percent-encoded, or generated URL-safe in the first place (openssl rand -hex 32).'
    );
  }
}

assertUsable(connectionString);

export const pool = new Pool({ connectionString });
