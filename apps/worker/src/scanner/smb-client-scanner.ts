import type { ScannedFile, SmbScanner, WorkerSmbSource } from '../pipeline.js';
import { SmbClientCliAdapter, type SmbClientAdapter } from './smb-client-adapter.js';
import { parseUncPath, resolveSecretFromRef } from './smb-path.js';

export class SmbClientScanner implements SmbScanner {
  constructor(private readonly client: SmbClientAdapter = new SmbClientCliAdapter()) {}

  async scanSource(source: WorkerSmbSource): Promise<ScannedFile[]> {
    const parsedPath = parseUncPath(source.path);
    if (!parsedPath) {
      return [];
    }

    const password = resolveSecretFromRef(source.secretRef);
    if (!password) {
      // eslint-disable-next-line no-console
      console.warn(
        JSON.stringify({
          service: 'worker',
          event: 'smb_secret_missing',
          sourceId: source.id,
          secretRef: source.secretRef
        })
      );
      return [];
    }

    try {
      const fileNames = await this.client.listPdfFiles({
        sourcePath: parsedPath,
        auth: {
          domainUsername: source.domainUsername,
          password
        }
      });

      const scanned: ScannedFile[] = [];
      for (const fileName of fileNames) {
        const content = await this.client.readPdf({
          sourcePath: parsedPath,
          auth: {
            domainUsername: source.domainUsername,
            password
          },
          fileName
        });

        scanned.push({
          sourceId: source.id,
          path: `${source.path.replace(/[\\/]+$/, '')}\\${fileName}`,
          content,
          modifiedAt: null
        });
      }

      return scanned;
    } catch (error) {
      // eslint-disable-next-line no-console
      console.warn(
        JSON.stringify({
          service: 'worker',
          event: 'smb_scan_failed',
          sourceId: source.id,
          message: error instanceof Error ? error.message : 'SMB_SCAN_FAILED'
        })
      );
      return [];
    }
  }
}
