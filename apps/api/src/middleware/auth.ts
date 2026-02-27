import type { NextFunction, Request, Response } from 'express';
import { verifyAccessToken } from '../auth/jwt.js';
import type { Role } from '@printo/shared';

declare global {
  namespace Express {
    interface Request {
      user?: {
        id: string;
        username: string;
        roles: Role[];
      };
    }
  }
}

export function requireAuth(req: Request, res: Response, next: NextFunction) {
  const auth = req.header('authorization');
  if (!auth?.startsWith('Bearer ')) {
    return res.status(401).json({ error: 'UNAUTHORIZED' });
  }

  const token = auth.slice('Bearer '.length);
  try {
    const claims = verifyAccessToken(token);
    req.user = {
      id: claims.sub,
      username: claims.username,
      roles: claims.roles
    };
    return next();
  } catch {
    return res.status(401).json({ error: 'INVALID_TOKEN' });
  }
}

export function requireRole(...roles: Role[]) {
  return (req: Request, res: Response, next: NextFunction) => {
    if (!req.user) {
      return res.status(401).json({ error: 'UNAUTHORIZED' });
    }

    const allowed = req.user.roles.some((role) => roles.includes(role));
    if (!allowed) {
      return res.status(403).json({ error: 'FORBIDDEN' });
    }

    return next();
  };
}
