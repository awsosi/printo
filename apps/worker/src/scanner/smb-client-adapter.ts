import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { parseDomainUsername, type ParsedUncPath } from './smb-path.js';

const execFileAsync = promisify(execFile);

interface SmbClientAuth {
  domainUsername: string;
  password: string;
}

export interface SmbClientAdapter {
  listPdfFiles(input: {
    sourcePath: ParsedUncPath;
    auth: SmbClientAuth;
  }): Promise<string[]>;
  readPdf(input: {
    sourcePath: ParsedUncPath;
    auth: SmbClientAuth;
    fileName: string;
  }): Promise<Buffer>;
}

function quoteSmbToken(value: string): string {
  return `"${value.replace(/"/g, '""')}"`;
}

function smbRemote(sourcePath: ParsedUncPath): string {
  return `//${sourcePath.server}/${sourcePath.share}`;
}

function buildAuthArgs(auth: SmbClientAuth): string[] {
  const parsedUser = parseDomainUsername(auth.domainUsername);

  const user = parsedUser.domain ? `${parsedUser.domain}\\${parsedUser.username}` : parsedUser.username;
  return ['-U', `${user}%${auth.password}`];
}

function buildDirectoryArgs(sourcePath: ParsedUncPath): string[] {
  if (!sourcePath.directory) {
    return [];
  }

  return ['-D', sourcePath.directory];
}

export class SmbClientCliAdapter implements SmbClientAdapter {
  constructor(private readonly smbClientBin = process.env.SMBCLIENT_BIN ?? 'smbclient') {}

  async listPdfFiles(input: {
    sourcePath: ParsedUncPath;
    auth: SmbClientAuth;
  }): Promise<string[]> {
    const args = [
      smbRemote(input.sourcePath),
      ...buildAuthArgs(input.auth),
      ...buildDirectoryArgs(input.sourcePath),
      '-g',
      '-c',
      'recurse off;ls'
    ];

    const { stdout } = await execFileAsync(this.smbClientBin, args, {
      encoding: 'utf8',
      maxBuffer: 10 * 1024 * 1024
    });

    return stdout
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.startsWith('file|'))
      .map((line) => line.split('|')[1]?.trim() ?? '')
      .filter((name) => name.toLowerCase().endsWith('.pdf'));
  }

  async readPdf(input: {
    sourcePath: ParsedUncPath;
    auth: SmbClientAuth;
    fileName: string;
  }): Promise<Buffer> {
    const tempDir = await mkdtemp(path.join(tmpdir(), 'printo-smb-'));
    const localPath = path.join(tempDir, path.basename(input.fileName));

    try {
      const command = `get ${quoteSmbToken(input.fileName)} ${quoteSmbToken(localPath)}`;
      const args = [
        smbRemote(input.sourcePath),
        ...buildAuthArgs(input.auth),
        ...buildDirectoryArgs(input.sourcePath),
        '-c',
        command
      ];

      await execFileAsync(this.smbClientBin, args, {
        encoding: 'utf8',
        maxBuffer: 10 * 1024 * 1024
      });

      return await readFile(localPath);
    } finally {
      await rm(tempDir, { recursive: true, force: true });
    }
  }
}
