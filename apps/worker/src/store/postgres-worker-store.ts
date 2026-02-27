import type { Pool } from 'pg';
import type {
  PrintJobPageRecord,
  PrintJobRecord,
  ProcessedFileRecord,
  WorkerConfigStore,
  WorkerFilenameMask,
  WorkerOcrGlobalConfig,
  WorkerOcrUserOverride,
  WorkerPrinter,
  WorkerRoutingProfile,
  WorkerSmbSource
} from '../pipeline.js';

type SmbSourceRow = {
  id: string;
  owner_user_id: string | null;
  path: string;
  domain_username: string;
  secret_ref: string;
  is_active: boolean;
};

type FilenameMaskRow = {
  id: string;
  owner_user_id: string | null;
  pattern: string;
  is_regex: boolean;
  is_active: boolean;
};

type PrinterRow = {
  id: string;
  name: string;
  type: 'A4' | 'THERMAL';
  target_uri: string;
  is_active: boolean;
};

type RoutingProfileRow = {
  id: string;
  name: string;
  thermal_label_patterns: unknown;
  fallback_printer_id: string | null;
};

type OcrGlobalRow = {
  provider: string;
  config: unknown;
};

type OcrOverrideRow = {
  user_id: string;
  provider: string | null;
  config: unknown;
};

type ProcessedFileRow = {
  id: string;
  source_id: string | null;
  file_path: string;
  checksum_sha256: string;
  file_mtime: Date | null;
};

type PrintJobRow = {
  id: string;
  source_file_id: string | null;
  status: string;
  error_message: string | null;
};

function toObject(value: unknown): Record<string, unknown> {
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }

  return {};
}

function toStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.filter((entry): entry is string => typeof entry === 'string');
}

function mapSmbSource(row: SmbSourceRow): WorkerSmbSource {
  return {
    id: row.id,
    ownerUserId: row.owner_user_id,
    path: row.path,
    domainUsername: row.domain_username,
    secretRef: row.secret_ref,
    isActive: row.is_active
  };
}

function mapFilenameMask(row: FilenameMaskRow): WorkerFilenameMask {
  return {
    id: row.id,
    ownerUserId: row.owner_user_id,
    pattern: row.pattern,
    isRegex: row.is_regex,
    isActive: row.is_active
  };
}

function mapPrinter(row: PrinterRow): WorkerPrinter {
  return {
    id: row.id,
    name: row.name,
    type: row.type,
    targetUri: row.target_uri,
    isActive: row.is_active
  };
}

function mapRoutingProfile(row: RoutingProfileRow): WorkerRoutingProfile {
  return {
    id: row.id,
    name: row.name,
    thermalLabelPatterns: toStringArray(row.thermal_label_patterns),
    fallbackPrinterId: row.fallback_printer_id
  };
}

export class PostgresWorkerStore implements WorkerConfigStore {
  constructor(private readonly db: Pool) {}

  async listActiveSmbSources(): Promise<WorkerSmbSource[]> {
    const result = await this.db.query<SmbSourceRow>(
      `SELECT id, owner_user_id, path, domain_username, secret_ref, is_active
       FROM smb_sources
       WHERE is_active = TRUE
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapSmbSource);
  }

  async listActiveFilenameMasks(ownerUserId: string | null): Promise<WorkerFilenameMask[]> {
    const result = await this.db.query<FilenameMaskRow>(
      `SELECT id, owner_user_id, pattern, is_regex, is_active
       FROM filename_masks
       WHERE is_active = TRUE
         AND (owner_user_id IS NULL OR ($1::uuid IS NOT NULL AND owner_user_id = $1::uuid))
       ORDER BY created_at ASC`,
      [ownerUserId]
    );

    return result.rows.map(mapFilenameMask);
  }

  async getRoutingProfile(_ownerUserId: string | null): Promise<WorkerRoutingProfile | null> {
    const result = await this.db.query<RoutingProfileRow>(
      `SELECT id, name, thermal_label_patterns, fallback_printer_id
       FROM routing_profiles
       ORDER BY created_at ASC
       LIMIT 1`
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapRoutingProfile(result.rows[0]);
  }

  async getActivePrinters(): Promise<WorkerPrinter[]> {
    const result = await this.db.query<PrinterRow>(
      `SELECT id, name, type, target_uri, is_active
       FROM printers
       WHERE is_active = TRUE
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapPrinter);
  }

  async getOcrGlobalConfig(): Promise<WorkerOcrGlobalConfig> {
    const result = await this.db.query<OcrGlobalRow>(
      `SELECT provider, config
       FROM ocr_config_global
       WHERE id = TRUE
       LIMIT 1`
    );

    if (!result.rows[0]) {
      return {
        provider: 'mock',
        config: {}
      };
    }

    return {
      provider: result.rows[0].provider,
      config: toObject(result.rows[0].config)
    };
  }

  async getOcrUserOverride(userId: string): Promise<WorkerOcrUserOverride | null> {
    const result = await this.db.query<OcrOverrideRow>(
      `SELECT user_id, provider, config
       FROM ocr_config_user_override
       WHERE user_id = $1
       LIMIT 1`,
      [userId]
    );

    if (!result.rows[0]) {
      return null;
    }

    return {
      userId: result.rows[0].user_id,
      provider: result.rows[0].provider,
      config: toObject(result.rows[0].config)
    };
  }

  async isProcessedFile(input: {
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<boolean> {
    const result = await this.db.query<{ exists: number }>(
      `SELECT 1 AS exists
       FROM processed_files
       WHERE checksum_sha256 = $1
          OR (
            file_path = $2
            AND ((file_mtime IS NULL AND $3::timestamptz IS NULL) OR file_mtime = $3)
          )
       LIMIT 1`,
      [input.checksumSha256, input.filePath, input.fileMtime]
    );

    return Boolean(result.rows[0]);
  }

  async markProcessedFile(input: {
    sourceId: string;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<ProcessedFileRecord> {
    try {
      const result = await this.db.query<ProcessedFileRow>(
        `INSERT INTO processed_files(source_id, file_path, checksum_sha256, file_mtime)
         VALUES ($1, $2, $3, $4)
         RETURNING id, source_id, file_path, checksum_sha256, file_mtime`,
        [input.sourceId, input.filePath, input.checksumSha256, input.fileMtime]
      );

      const row = result.rows[0];
      return {
        id: row.id,
        sourceId: row.source_id ?? input.sourceId,
        filePath: row.file_path,
        checksumSha256: row.checksum_sha256,
        fileMtime: row.file_mtime
      };
    } catch (error) {
      const databaseError = error as { code?: string };
      if (databaseError.code !== '23505') {
        throw error;
      }

      const existing = await this.db.query<ProcessedFileRow>(
        `SELECT id, source_id, file_path, checksum_sha256, file_mtime
         FROM processed_files
         WHERE checksum_sha256 = $1
            OR (
              file_path = $2
              AND ((file_mtime IS NULL AND $3::timestamptz IS NULL) OR file_mtime = $3)
            )
         ORDER BY processed_at DESC
         LIMIT 1`,
        [input.checksumSha256, input.filePath, input.fileMtime]
      );

      const row = existing.rows[0];
      if (!row) {
        throw error;
      }

      return {
        id: row.id,
        sourceId: row.source_id ?? input.sourceId,
        filePath: row.file_path,
        checksumSha256: row.checksum_sha256,
        fileMtime: row.file_mtime
      };
    }
  }

  async createPrintJob(input: {
    sourceId: string;
    sourceFileId: string | null;
    filePath: string;
  }): Promise<PrintJobRecord> {
    const result = await this.db.query<PrintJobRow>(
      `INSERT INTO print_jobs(source_file_id, status, error_message)
       VALUES ($1, 'PENDING', NULL)
       RETURNING id, source_file_id, status, error_message`,
      [input.sourceFileId]
    );

    const row = result.rows[0];

    return {
      id: row.id,
      sourceId: input.sourceId,
      sourceFileId: row.source_file_id,
      filePath: input.filePath,
      status: row.status === 'FAILURE' ? 'FAILURE' : row.status === 'SUCCESS' ? 'SUCCESS' : 'PENDING',
      errorMessage: row.error_message
    };
  }

  async addPrintJobPage(input: PrintJobPageRecord): Promise<void> {
    await this.db.query(
      `INSERT INTO print_job_pages(print_job_id, page_number, route_type, printer_id, status)
       VALUES ($1, $2, $3, $4, $5)`,
      [input.printJobId, input.pageNumber, input.routeType, input.printerId, input.status]
    );
  }

  async finishPrintJob(input: {
    jobId: string;
    status: 'SUCCESS' | 'FAILURE';
    errorMessage?: string;
  }): Promise<void> {
    await this.db.query(
      `UPDATE print_jobs
       SET status = $2,
           error_message = $3,
           updated_at = NOW()
       WHERE id = $1`,
      [input.jobId, input.status, input.errorMessage ?? null]
    );
  }

  async linkProcessedFileToJob(input: { jobId: string; sourceFileId: string }): Promise<void> {
    await this.db.query(
      `UPDATE print_jobs
       SET source_file_id = $2,
           updated_at = NOW()
       WHERE id = $1`,
      [input.jobId, input.sourceFileId]
    );
  }
}
