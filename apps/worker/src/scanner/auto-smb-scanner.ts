import type { SmbScanner, ScannedFile, WorkerSmbSource } from '../pipeline.js';
import { FilesystemSmbScanner } from './filesystem-smb-scanner.js';
import { SmbClientScanner } from './smb-client-scanner.js';
import { parseUncPath } from './smb-path.js';

export class AutoSmbScanner implements SmbScanner {
  constructor(
    private readonly filesystemScanner: SmbScanner = new FilesystemSmbScanner(),
    private readonly smbClientScanner: SmbScanner = new SmbClientScanner()
  ) {}

  async scanSource(source: WorkerSmbSource): Promise<ScannedFile[]> {
    if (parseUncPath(source.path)) {
      return this.smbClientScanner.scanSource(source);
    }

    return this.filesystemScanner.scanSource(source);
  }
}
