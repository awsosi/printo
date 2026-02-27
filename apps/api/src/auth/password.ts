import { createHash } from 'node:crypto';
import bcrypt from 'bcryptjs';

type HashAlgorithm = 'argon2id' | 'bcrypt';

type Argon2Module = {
  argon2id: number;
  hash(value: string, options: { type: number }): Promise<string>;
  verify(hash: string, plainText: string): Promise<boolean>;
};

async function tryArgon2(): Promise<Argon2Module | null> {
  try {
    const moduleName = 'argon2';
    const argon2 = (await import(moduleName)) as unknown as Argon2Module;
    return argon2;
  } catch {
    return null;
  }
}

export async function hashPassword(plainText: string): Promise<{ hash: string; algorithm: HashAlgorithm }> {
  const strategy = process.env.AUTH_HASH_STRATEGY ?? 'auto';
  const argon2 = strategy !== 'bcrypt' ? await tryArgon2() : null;

  if (argon2) {
    return {
      hash: await argon2.hash(plainText, { type: argon2.argon2id }),
      algorithm: 'argon2id'
    };
  }

  return {
    hash: await bcrypt.hash(plainText, 12),
    algorithm: 'bcrypt'
  };
}

export async function verifyPassword(plainText: string, hash: string, algorithm: string): Promise<boolean> {
  if (algorithm === 'argon2id') {
    const argon2 = await tryArgon2();
    if (!argon2) {
      return false;
    }
    return argon2.verify(hash, plainText);
  }

  return bcrypt.compare(plainText, hash);
}

export function hashToken(token: string): string {
  return createHash('sha256').update(token).digest('hex');
}
