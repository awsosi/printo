import jwt from 'jsonwebtoken';
import type { SignOptions } from 'jsonwebtoken';
import type { JwtClaims, Role } from '@printo/shared';

const accessSecret = process.env.JWT_ACCESS_SECRET ?? 'dev-access-secret';
const refreshSecret = process.env.JWT_REFRESH_SECRET ?? 'dev-refresh-secret';
const accessTtl = (process.env.JWT_ACCESS_TTL ?? '15m') as SignOptions['expiresIn'];
const refreshTtl = (process.env.JWT_REFRESH_TTL ?? '7d') as SignOptions['expiresIn'];

export function signAccessToken(input: { sub: string; username: string; roles: Role[] }): string {
  return jwt.sign({ ...input, type: 'access' }, accessSecret, { expiresIn: accessTtl });
}

export function signRefreshToken(input: { sub: string; username: string; roles: Role[] }): string {
  return jwt.sign({ ...input, type: 'refresh' }, refreshSecret, { expiresIn: refreshTtl });
}

export function verifyAccessToken(token: string): JwtClaims {
  return jwt.verify(token, accessSecret) as JwtClaims;
}

export function verifyRefreshToken(token: string): JwtClaims {
  return jwt.verify(token, refreshSecret) as JwtClaims;
}
