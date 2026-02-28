export interface ParsedUncPath {
  server: string;
  share: string;
  directory: string;
}

export interface ParsedDomainUser {
  domain: string | null;
  username: string;
}

export function parseUncPath(input: string): ParsedUncPath | null {
  if (!input.startsWith('\\\\')) {
    return null;
  }

  const pathBody = input.replace(/^\\+/, '');
  const parts = pathBody.split('\\').filter(Boolean);

  if (parts.length < 2) {
    return null;
  }

  const [server, share, ...rest] = parts;
  return {
    server,
    share,
    directory: rest.join('\\')
  };
}

export function parseDomainUsername(input: string): ParsedDomainUser {
  const trimmed = input.trim();
  const separator = trimmed.indexOf('\\');

  if (separator === -1) {
    return {
      domain: null,
      username: trimmed
    };
  }

  const domain = trimmed.slice(0, separator).trim();
  const username = trimmed.slice(separator + 1).trim();

  return {
    domain: domain || null,
    username
  };
}

export function resolveSecretFromRef(secretRef: string): string | null {
  const trimmed = secretRef.trim();
  if (!trimmed) {
    return null;
  }

  if (trimmed.startsWith('plain:')) {
    const value = trimmed.slice('plain:'.length);
    return value.length > 0 ? value : null;
  }

  if (trimmed.startsWith('env:')) {
    const envKey = trimmed.slice('env:'.length).trim();
    if (!envKey) {
      return null;
    }
    return process.env[envKey] ?? null;
  }

  const normalizedKey = trimmed.replace(/[^a-zA-Z0-9]/g, '_').toUpperCase();
  const mapped = process.env[`WORKER_SECRET_${normalizedKey}`];
  if (mapped) {
    return mapped;
  }

  return process.env[trimmed] ?? null;
}
