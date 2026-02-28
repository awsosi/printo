import { describe, expect, it } from 'vitest';
import { parseDomainUsername, parseUncPath, resolveSecretFromRef } from '../src/scanner/smb-path.js';

describe('smb path helpers', () => {
  it('parses UNC path with nested directory', () => {
    const parsed = parseUncPath('\\\\fileserver\\print\\incoming\\a');

    expect(parsed).toEqual({
      server: 'fileserver',
      share: 'print',
      directory: 'incoming\\a'
    });
  });

  it('parses domain user and plain user', () => {
    expect(parseDomainUsername('EXAMPLE\\serviceuser')).toEqual({
      domain: 'EXAMPLE',
      username: 'serviceuser'
    });

    expect(parseDomainUsername('serviceuser')).toEqual({
      domain: null,
      username: 'serviceuser'
    });
  });

  it('resolves secret from env references and mapped keys', () => {
    process.env.SMB_PASS = 'env-secret';
    process.env.WORKER_SECRET_SECRET___SMB__SERVICE = 'mapped-secret';

    expect(resolveSecretFromRef('env:SMB_PASS')).toBe('env-secret');
    expect(resolveSecretFromRef('secret://smb//service')).toBe('mapped-secret');

    delete process.env.SMB_PASS;
    delete process.env.WORKER_SECRET_SECRET___SMB__SERVICE;
  });
});
