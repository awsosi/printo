import { ROLES } from '@printo/shared';
import type { Role } from '@printo/shared';
import { hashPassword, hashToken, verifyPassword } from './password.js';
import { signAccessToken, signRefreshToken, verifyRefreshToken } from './jwt.js';
import type { AuthStore } from '../store/auth-store.js';

export class AuthService {
  constructor(private readonly store: AuthStore) {}

  async register(input: { username: string; password: string; roles?: Role[] }) {
    const { hash, algorithm } = await hashPassword(input.password);
    const roles = input.roles && input.roles.length > 0 ? input.roles : [ROLES.USER];
    const user = await this.store.createUser({
      username: input.username,
      passwordHash: hash,
      hashAlgorithm: algorithm,
      roles
    });

    await this.store.writeAuditEvent({
      actorUserId: user.id,
      action: 'AUTH_REGISTER',
      status: 'SUCCESS',
      targetType: 'USER',
      targetId: user.id,
      metadata: { roles }
    });

    return { id: user.id, username: user.username, roles: user.roles };
  }

  async login(input: { username: string; password: string }) {
    const user = await this.store.getUserByUsername(input.username);
    if (!user) {
      await this.store.writeAuditEvent({
        action: 'AUTH_LOGIN',
        status: 'FAILURE',
        metadata: { username: input.username, reason: 'USER_NOT_FOUND' }
      });
      throw new Error('INVALID_CREDENTIALS');
    }

    const validPassword = await verifyPassword(input.password, user.passwordHash, user.hashAlgorithm);
    if (!validPassword) {
      await this.store.writeAuditEvent({
        actorUserId: user.id,
        action: 'AUTH_LOGIN',
        status: 'FAILURE',
        metadata: { username: input.username, reason: 'BAD_PASSWORD' }
      });
      throw new Error('INVALID_CREDENTIALS');
    }

    const accessToken = signAccessToken({ sub: user.id, username: user.username, roles: user.roles });
    const refreshToken = signRefreshToken({ sub: user.id, username: user.username, roles: user.roles });
    const refreshTokenHash = hashToken(refreshToken);
    const refreshExpiry = new Date(Date.now() + 7 * 24 * 3600 * 1000);
    await this.store.saveRefreshToken({ userId: user.id, tokenHash: refreshTokenHash, expiresAt: refreshExpiry });

    await this.store.writeAuditEvent({
      actorUserId: user.id,
      action: 'AUTH_LOGIN',
      status: 'SUCCESS'
    });

    return {
      accessToken,
      refreshToken,
      user: {
        id: user.id,
        username: user.username,
        roles: user.roles
      }
    };
  }

  async refresh(refreshToken: string) {
    const claims = verifyRefreshToken(refreshToken);
    const tokenHash = hashToken(refreshToken);
    const stored = await this.store.getRefreshTokenByHash(tokenHash);

    if (!stored || stored.revokedAt || stored.expiresAt < new Date()) {
      await this.store.writeAuditEvent({
        actorUserId: claims.sub,
        action: 'AUTH_REFRESH',
        status: 'FAILURE',
        metadata: { reason: 'TOKEN_INVALID' }
      });
      throw new Error('INVALID_REFRESH_TOKEN');
    }

    await this.store.revokeRefreshToken(tokenHash);
    const user = await this.store.getUserById(claims.sub);
    if (!user) {
      throw new Error('USER_NOT_FOUND');
    }

    const newAccessToken = signAccessToken({ sub: user.id, username: user.username, roles: user.roles });
    const newRefreshToken = signRefreshToken({ sub: user.id, username: user.username, roles: user.roles });
    await this.store.saveRefreshToken({
      userId: user.id,
      tokenHash: hashToken(newRefreshToken),
      expiresAt: new Date(Date.now() + 7 * 24 * 3600 * 1000)
    });

    await this.store.writeAuditEvent({
      actorUserId: user.id,
      action: 'AUTH_REFRESH',
      status: 'SUCCESS'
    });

    return { accessToken: newAccessToken, refreshToken: newRefreshToken };
  }
}
