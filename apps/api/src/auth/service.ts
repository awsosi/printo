import { ROLES } from '@printo/shared';
import type { Role } from '@printo/shared';
import { signAccessToken, signRefreshToken, verifyRefreshToken } from './jwt.js';
import { authenticateExternal } from './external-auth-adapter.js';
import { hashPassword, hashToken, verifyPassword } from './password.js';
import type { AuthStore } from '../store/auth-store.js';

export class AuthService {
  constructor(private readonly store: AuthStore) {}

  async register(input: { username: string; password: string; roles?: Role[]; isRemoteEnabled?: boolean }) {
    const { hash, algorithm } = await hashPassword(input.password);
    const roles = input.roles && input.roles.length > 0 ? input.roles : [ROLES.USER];
    const user = await this.store.createUser({
      username: input.username,
      passwordHash: hash,
      hashAlgorithm: algorithm,
      roles,
      isRemoteEnabled: input.isRemoteEnabled ?? false
    });

    await this.store.writeAuditEvent({
      actorUserId: user.id,
      action: 'AUTH_REGISTER',
      status: 'SUCCESS',
      targetType: 'USER',
      targetId: user.id,
      metadata: { roles, isRemoteEnabled: user.isRemoteEnabled }
    });

    return {
      id: user.id,
      username: user.username,
      roles: user.roles,
      isRemoteEnabled: user.isRemoteEnabled
    };
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

    if (user.isRemoteEnabled) {
      const remoteResult = await authenticateExternal(input.username, input.password);
      const remoteAttemptStatus = remoteResult.ok && remoteResult.authenticated ? 'SUCCESS' : 'FAILURE';

      await this.store.writeAuditEvent({
        actorUserId: user.id,
        action: 'AUTH_REMOTE_ATTEMPT',
        status: remoteAttemptStatus,
        targetType: 'USER',
        targetId: user.id,
        metadata: {
          username: input.username,
          ok: remoteResult.ok,
          authenticated: remoteResult.authenticated,
          reason: remoteResult.reason
        }
      });

      if (!remoteResult.ok || !remoteResult.authenticated) {
        await this.store.writeAuditEvent({
          actorUserId: user.id,
          action: 'AUTH_LOGIN',
          status: 'FAILURE',
          metadata: {
            username: input.username,
            mode: 'remote',
            reason: remoteResult.reason ?? 'REMOTE_AUTH_FAILED'
          }
        });
        throw new Error('INVALID_CREDENTIALS');
      }
    } else {
      if (!user.passwordHash || !user.hashAlgorithm) {
        await this.store.writeAuditEvent({
          actorUserId: user.id,
          action: 'AUTH_LOGIN',
          status: 'FAILURE',
          metadata: { username: input.username, reason: 'MISSING_LOCAL_CREDENTIALS' }
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
    }

    const accessToken = signAccessToken({ sub: user.id, username: user.username, roles: user.roles });
    const refreshToken = signRefreshToken({ sub: user.id, username: user.username, roles: user.roles });
    const refreshTokenHash = hashToken(refreshToken);
    const refreshExpiry = new Date(Date.now() + 7 * 24 * 3600 * 1000);
    await this.store.saveRefreshToken({ userId: user.id, tokenHash: refreshTokenHash, expiresAt: refreshExpiry });

    await this.store.writeAuditEvent({
      actorUserId: user.id,
      action: 'AUTH_LOGIN',
      status: 'SUCCESS',
      metadata: { mode: user.isRemoteEnabled ? 'remote' : 'local' }
    });

    return {
      accessToken,
      refreshToken,
      user: {
        id: user.id,
        username: user.username,
        roles: user.roles,
        isRemoteEnabled: user.isRemoteEnabled
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
