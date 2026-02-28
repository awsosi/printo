import { describe, expect, it } from 'vitest';
import { SmbClientScanner } from '../src/scanner/smb-client-scanner.js';

describe('smb client scanner', () => {
  it('scans UNC source using adapter and resolves secret from env', async () => {
    process.env.SMB_PASS = 'TopSecret!';

    const scanner = new SmbClientScanner({
      listPdfFiles: async ({ auth }) => {
        expect(auth.password).toBe('TopSecret!');
        expect(auth.domainUsername).toBe('EXAMPLE\\serviceuser');
        return ['a.pdf', 'b.pdf'];
      },
      readPdf: async ({ fileName }) => Buffer.from(`content-${fileName}`)
    });

    const scanned = await scanner.scanSource({
      id: 'smb-1',
      ownerUserId: null,
      path: '\\\\fileserver\\print\\incoming',
      domainUsername: 'EXAMPLE\\serviceuser',
      secretRef: 'env:SMB_PASS',
      isActive: true
    });

    expect(scanned).toHaveLength(2);
    expect(scanned[0]?.path).toBe('\\\\fileserver\\print\\incoming\\a.pdf');
    expect(scanned[1]?.content.toString('utf8')).toBe('content-b.pdf');

    delete process.env.SMB_PASS;
  });

  it('returns empty list when source path is not UNC', async () => {
    const scanner = new SmbClientScanner({
      listPdfFiles: async () => ['x.pdf'],
      readPdf: async () => Buffer.from('x')
    });

    const scanned = await scanner.scanSource({
      id: 'smb-2',
      ownerUserId: null,
      path: '/local/path',
      domainUsername: 'EXAMPLE\\serviceuser',
      secretRef: 'plain:pass',
      isActive: true
    });

    expect(scanned).toHaveLength(0);
  });
});
