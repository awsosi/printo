import { readdir, readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import type { ScannedFile, SmbScanner, WorkerSmbSource } from '../pipeline.js';

function normalizeSourcePath(sourcePath: string): string | null {
  if (!sourcePath) {
    return null;
  }

  if (sourcePath.startsWith('file://')) {
    try {
      return new URL(sourcePath).pathname;
    } catch {
      return null;
    }
  }

  if (sourcePath.startsWith('\\\\')) {
    return null;
  }

  return sourcePath;
}

export class FilesystemSmbScanner implements SmbScanner {
  async scanSource(source: WorkerSmbSource): Promise<ScannedFile[]> {
    const directoryPath = normalizeSourcePath(source.path);
    if (!directoryPath) {
      return [];
    }

    try {
      const entries = await readdir(directoryPath, { withFileTypes: true });
      const files = entries.filter((entry) => entry.isFile() && entry.name.toLowerCase().endsWith('.pdf'));

      const scanned: ScannedFile[] = [];
      for (const file of files) {
        const filePath = path.join(directoryPath, file.name);
        const [content, fileStats] = await Promise.all([readFile(filePath), stat(filePath)]);

        scanned.push({
          sourceId: source.id,
          path: filePath,
          content,
          modifiedAt: fileStats.mtime
        });
      }

      return scanned;
    } catch {
      return [];
    }
  }
}
