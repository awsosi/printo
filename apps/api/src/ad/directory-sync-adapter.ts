import type { AdDiscoverySnapshot, AdSyncConfigRecord, PrinterType } from '../types.js';

interface RawAdUser {
  id?: string;
  username?: string;
  displayName?: string;
}

interface RawAdGroup {
  id?: string;
  name?: string;
  memberUsernames?: unknown;
}

interface RawAdSmbShare {
  id?: string;
  path?: string;
  domainUsername?: string;
}

interface RawAdPrinter {
  id?: string;
  name?: string;
  targetUri?: string;
  type?: string;
}

interface RawAdSnapshot {
  users?: unknown;
  groups?: unknown;
  smbShares?: unknown;
  printers?: unknown;
}

function parseArray<T>(value: unknown): T[] {
  return Array.isArray(value) ? (value as T[]) : [];
}

function normalizePrinterType(value: string | undefined): PrinterType | undefined {
  if (!value) {
    return undefined;
  }

  const normalized = value.trim().toUpperCase();
  if (normalized === 'A4' || normalized === 'THERMAL') {
    return normalized;
  }
  return undefined;
}

function normalizeSnapshot(raw: RawAdSnapshot): AdDiscoverySnapshot {
  const users = parseArray<RawAdUser>(raw.users)
    .map((entry, index) => {
      const username = (entry.username ?? '').trim();
      if (!username) {
        return null;
      }

      return {
        id: (entry.id ?? username ?? `user-${index + 1}`).toString(),
        username,
        displayName: (entry.displayName ?? username).toString()
      };
    })
    .filter((entry): entry is NonNullable<typeof entry> => entry !== null);

  const groups = parseArray<RawAdGroup>(raw.groups)
    .map((entry, index) => {
      const name = (entry.name ?? '').trim();
      if (!name) {
        return null;
      }
      const members = Array.isArray(entry.memberUsernames)
        ? entry.memberUsernames.filter((member): member is string => typeof member === 'string')
        : [];
      return {
        id: (entry.id ?? name ?? `group-${index + 1}`).toString(),
        name,
        memberUsernames: members
      };
    })
    .filter((entry): entry is NonNullable<typeof entry> => entry !== null);

  const smbShares = parseArray<RawAdSmbShare>(raw.smbShares)
    .map((entry, index) => {
      const path = (entry.path ?? '').trim();
      if (!path) {
        return null;
      }
      return {
        id: (entry.id ?? path ?? `smb-${index + 1}`).toString(),
        path,
        domainUsername: entry.domainUsername?.trim() || undefined
      };
    })
    .filter((entry): entry is NonNullable<typeof entry> => entry !== null);

  const printers = parseArray<RawAdPrinter>(raw.printers)
    .map((entry, index) => {
      const name = (entry.name ?? '').trim();
      if (!name) {
        return null;
      }
      return {
        id: (entry.id ?? name ?? `printer-${index + 1}`).toString(),
        name,
        targetUri: entry.targetUri?.trim() || undefined,
        type: normalizePrinterType(entry.type)
      };
    })
    .filter((entry): entry is NonNullable<typeof entry> => entry !== null);

  return { users, groups, smbShares, printers };
}

function resolveSecretFromRef(secretRef: string): string | null {
  if (!secretRef) {
    return null;
  }

  if (secretRef.startsWith('env:')) {
    const key = secretRef.slice('env:'.length).trim();
    return key ? process.env[key] ?? null : null;
  }

  return process.env[secretRef] ?? null;
}

function toPositiveInteger(value: string | undefined, fallback: number): number {
  if (!value) {
    return fallback;
  }

  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed <= 0) {
    return fallback;
  }

  return Math.trunc(parsed);
}

export async function discoverDirectorySnapshot(input: {
  config: AdSyncConfigRecord;
  bindPassword?: string;
}): Promise<AdDiscoverySnapshot> {
  const mockSnapshot = process.env.AD_SYNC_MOCK_DATA_JSON;
  if (mockSnapshot) {
    const parsed = JSON.parse(mockSnapshot) as RawAdSnapshot;
    return normalizeSnapshot(parsed);
  }

  const upstreamBase = process.env.AD_SYNC_API_BASE_URL || input.config.serverUrl;
  if (!upstreamBase) {
    return {
      users: [],
      groups: [],
      smbShares: [],
      printers: []
    };
  }

  const timeoutMs = toPositiveInteger(process.env.AD_SYNC_TIMEOUT_MS, 6000);
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);

  try {
    const url = new URL('/directory/snapshot', upstreamBase);
    const resolvedPassword = input.bindPassword || resolveSecretFromRef(input.config.bindSecretRef) || '';

    const response = await fetch(url, {
      method: 'POST',
      signal: controller.signal,
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        domain: input.config.domain,
        baseDn: input.config.baseDn,
        bindUsername: input.config.bindUsername,
        bindPassword: resolvedPassword
      })
    });

    if (!response.ok) {
      throw new Error('AD_SYNC_UPSTREAM_REJECTED');
    }

    const raw = (await response.json()) as RawAdSnapshot;
    return normalizeSnapshot(raw);
  } catch (error) {
    if ((error as Error).name === 'AbortError') {
      throw new Error('AD_SYNC_TIMEOUT');
    }
    throw new Error('AD_SYNC_FAILED');
  } finally {
    clearTimeout(timeout);
  }
}
