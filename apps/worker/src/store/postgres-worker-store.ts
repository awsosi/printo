import type { Pool } from 'pg';
import type {
  PrintJobPageRecord,
  PrintJobRecord,
  ProcessedFileRecord,
  SuccessfulPageDispatchRecord,
  WorkerConfigStore,
  WorkerFilenameMask,
  WorkerOcrGlobalConfig,
  WorkerOcrUserOverride,
  WorkerPrinter,
  WorkerRoutingProfile,
  WorkerSmbSource,
  WorkerSystemSettings,
  WorkerUserPrinterAssignment,
  WorkerVisualProfile
} from '../pipeline.js';

type SmbSourceRow = {
  id: string;
  owner_user_id: string | null;
  owner_group_id: string | null;
  path: string;
  domain_username: string;
  secret_ref: string;
  printer_domain_username: string;
  printer_secret_ref: string;
  routing_profile_id: string | null;
  a4_printer_id: string | null;
  thermal_printer_id: string | null;
  include_filename_patterns: unknown;
  exclude_filename_patterns: unknown;
  is_active: boolean;
};

type FilenameMaskRow = {
  id: string;
  owner_user_id: string | null;
  owner_group_id: string | null;
  pattern: string;
  is_regex: boolean;
  is_active: boolean;
};

type PrinterRow = {
  id: string;
  name: string;
  type: 'A4' | 'THERMAL';
  target_uri: string;
  domain_username: string;
  secret_ref: string;
  is_active: boolean;
};

type SystemSettingsRow = {
  global_smb_domain_username: string;
  global_smb_secret_ref: string;
  global_printer_domain_username: string;
  global_printer_secret_ref: string;
  worker_poll_interval_ms: number;
  smtp_enabled: boolean;
  smtp_host: string;
  smtp_port: number;
  smtp_secure: boolean;
  smtp_username: string;
  smtp_secret_ref: string;
  smtp_from: string;
  smtp_to: unknown;
};

type RoutingProfileRow = {
  id: string;
  name: string;
  owner_user_id: string | null;
  owner_group_id: string | null;
  printer_domain_username: string;
  printer_secret_ref: string;
  default_route_type: 'A4' | 'THERMAL';
  thermal_label_patterns: unknown;
  fallback_printer_id: string | null;
  sample_pdf_name: string | null;
  sample_pdf_base64: string | null;
  snippet_base64: string | null;
  match_threshold: number;
  visual_rules: unknown;
  classification_routes: unknown;
};

type VisualProfileRow = {
  id: string;
  name: string;
  owner_user_id: string | null;
  owner_group_id: string | null;
  snippet_base64: string;
  match_mode: 'CONTAINS' | 'EXACT';
  route_type: 'A4' | 'THERMAL' | null;
  printer_id: string | null;
  labels: unknown;
  is_active: boolean;
};

type UserPrinterAssignmentRow = {
  user_id: string;
  a4_printer_id: string | null;
  thermal_printer_id: string | null;
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
  source_id: string | null;
  source_file_id: string | null;
  file_path: string;
  file_checksum_sha256: string;
  file_mtime: Date | null;
  is_cancelled: boolean;
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

function toRoutingVisualRules(value: unknown): WorkerRoutingProfile['visualRules'] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.flatMap((entry) => {
    if (!entry || typeof entry !== 'object') {
      return [];
    }
    const candidate = entry as Record<string, unknown>;
    const rect = candidate.rect;
    if (!rect || typeof rect !== 'object') {
      return [];
    }
    const rectRecord = rect as Record<string, unknown>;
    const x = Number(rectRecord.x);
    const y = Number(rectRecord.y);
    const width = Number(rectRecord.width);
    const height = Number(rectRecord.height);
    const samplePageNumber = Number(candidate.samplePageNumber);
    if (!Number.isFinite(samplePageNumber) || samplePageNumber < 1) {
      return [];
    }
    if (![x, y, width, height].every((part) => Number.isFinite(part) && part >= 0) || width <= 0 || height <= 0) {
      return [];
    }
    return [
      {
        id: typeof candidate.id === 'string' ? candidate.id : '',
        samplePageNumber,
        routeType: candidate.routeType === 'THERMAL' ? 'THERMAL' : 'A4',
        matchMode: candidate.matchMode === 'EXACT' ? 'EXACT' : 'CONTAINS',
        expectedText: typeof candidate.expectedText === 'string' ? candidate.expectedText : '',
        expectedWords: toStringArray(candidate.expectedWords),
        rect: { x, y, width, height }
      }
    ];
  });
}

const PAGE_CLASSES = ['OUTGOING_LABEL_THERMAL', 'RETURN_LABEL_A4', 'DOCUMENT_A4'] as const;

function toClassificationRoutes(value: unknown): NonNullable<WorkerRoutingProfile['classificationRoutes']> {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.flatMap((entry) => {
    if (!entry || typeof entry !== 'object') {
      return [];
    }
    const candidate = entry as Record<string, unknown>;
    if (!PAGE_CLASSES.includes(candidate.pageClass as (typeof PAGE_CLASSES)[number])) {
      return [];
    }
    const minConfidence = Number(candidate.minConfidence ?? 0);
    return [
      {
        pageClass: candidate.pageClass as (typeof PAGE_CLASSES)[number],
        routeType: candidate.routeType === 'THERMAL' ? ('THERMAL' as const) : ('A4' as const),
        printerId: typeof candidate.printerId === 'string' ? candidate.printerId : null,
        minConfidence: Number.isFinite(minConfidence) ? Math.min(1, Math.max(0, minConfidence)) : 0
      }
    ];
  });
}

function mapSmbSource(row: SmbSourceRow): WorkerSmbSource {
  return {
    id: row.id,
    ownerUserId: row.owner_user_id,
    ownerGroupId: row.owner_group_id,
    path: row.path,
    domainUsername: row.domain_username,
    secretRef: row.secret_ref,
    printerDomainUsername: row.printer_domain_username,
    printerSecretRef: row.printer_secret_ref,
    routingProfileId: row.routing_profile_id,
    a4PrinterId: row.a4_printer_id,
    thermalPrinterId: row.thermal_printer_id,
    includeFilenamePatterns: toStringArray(row.include_filename_patterns),
    excludeFilenamePatterns: toStringArray(row.exclude_filename_patterns),
    isActive: row.is_active
  };
}

function mapFilenameMask(row: FilenameMaskRow): WorkerFilenameMask {
  return {
    id: row.id,
    ownerUserId: row.owner_user_id,
    ownerGroupId: row.owner_group_id,
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
    domainUsername: row.domain_username,
    secretRef: row.secret_ref,
    isActive: row.is_active
  };
}

function mapSystemSettings(row: SystemSettingsRow): WorkerSystemSettings {
  return {
    globalSmbDomainUsername: row.global_smb_domain_username,
    globalSmbSecretRef: row.global_smb_secret_ref,
    globalPrinterDomainUsername: row.global_printer_domain_username,
    globalPrinterSecretRef: row.global_printer_secret_ref,
    workerPollIntervalMs: row.worker_poll_interval_ms,
    smtpEnabled: row.smtp_enabled,
    smtpHost: row.smtp_host,
    smtpPort: row.smtp_port,
    smtpSecure: row.smtp_secure,
    smtpUsername: row.smtp_username,
    smtpSecretRef: row.smtp_secret_ref,
    smtpFrom: row.smtp_from,
    smtpTo: toStringArray(row.smtp_to)
  };
}

function mapRoutingProfile(row: RoutingProfileRow): WorkerRoutingProfile {
  return {
    id: row.id,
    name: row.name,
    ownerUserId: row.owner_user_id,
    ownerGroupId: row.owner_group_id,
    printerDomainUsername: row.printer_domain_username,
    printerSecretRef: row.printer_secret_ref,
    defaultRouteType: row.default_route_type,
    thermalLabelPatterns: toStringArray(row.thermal_label_patterns),
    fallbackPrinterId: row.fallback_printer_id,
    samplePdfName: row.sample_pdf_name,
    samplePdfBase64: row.sample_pdf_base64,
    snippetBase64: row.snippet_base64,
    matchThreshold: row.match_threshold,
    visualRules: toRoutingVisualRules(row.visual_rules),
    classificationRoutes: toClassificationRoutes(row.classification_routes)
  };
}

function mapVisualProfile(row: VisualProfileRow): WorkerVisualProfile {
  return {
    id: row.id,
    name: row.name,
    ownerUserId: row.owner_user_id,
    ownerGroupId: row.owner_group_id,
    snippetBase64: row.snippet_base64,
    matchMode: row.match_mode,
    routeType: row.route_type,
    printerId: row.printer_id,
    labels: toStringArray(row.labels),
    isActive: row.is_active
  };
}

export class PostgresWorkerStore implements WorkerConfigStore {
  constructor(private readonly db: Pool) {}

  async listActiveSmbSources(): Promise<WorkerSmbSource[]> {
    const result = await this.db.query<SmbSourceRow>(
      `SELECT id, owner_user_id, owner_group_id, path, domain_username, secret_ref, printer_domain_username, printer_secret_ref,
              routing_profile_id, a4_printer_id, thermal_printer_id, include_filename_patterns, exclude_filename_patterns, is_active
       FROM smb_sources
       WHERE is_active = TRUE
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapSmbSource);
  }

  async getSmbSource(sourceId: string): Promise<WorkerSmbSource | null> {
    const result = await this.db.query<SmbSourceRow>(
      `SELECT id, owner_user_id, owner_group_id, path, domain_username, secret_ref, printer_domain_username, printer_secret_ref,
              routing_profile_id, a4_printer_id, thermal_printer_id, include_filename_patterns, exclude_filename_patterns, is_active
       FROM smb_sources
       WHERE id = $1
       LIMIT 1`,
      [sourceId]
    );

    return result.rows[0] ? mapSmbSource(result.rows[0]) : null;
  }

  async listActiveFilenameMasks(ownerUserId: string | null, ownerGroupId: string | null): Promise<WorkerFilenameMask[]> {
    const result = await this.db.query<FilenameMaskRow>(
      `SELECT id, owner_user_id, owner_group_id, pattern, is_regex, is_active
       FROM filename_masks
       WHERE is_active = TRUE
         AND (
           (owner_user_id IS NULL AND owner_group_id IS NULL)
           OR ($1::uuid IS NOT NULL AND owner_user_id = $1::uuid)
           OR ($2::uuid IS NOT NULL AND owner_group_id = $2::uuid)
         )
       ORDER BY created_at ASC`,
      [ownerUserId, ownerGroupId]
    );

    return result.rows.map(mapFilenameMask);
  }

  async getRoutingProfile(ownerUserId: string | null, ownerGroupId: string | null): Promise<WorkerRoutingProfile | null> {
    const result = await this.db.query<RoutingProfileRow>(
      `SELECT id, name, owner_user_id, owner_group_id, printer_domain_username, printer_secret_ref, default_route_type,
              thermal_label_patterns, fallback_printer_id, sample_pdf_name, sample_pdf_base64, snippet_base64, match_threshold, visual_rules, classification_routes
       FROM routing_profiles
       WHERE
         (owner_user_id IS NULL AND owner_group_id IS NULL)
         OR ($1::uuid IS NOT NULL AND owner_user_id = $1::uuid)
         OR ($2::uuid IS NOT NULL AND owner_group_id = $2::uuid)
       ORDER BY
         CASE
           WHEN $1::uuid IS NOT NULL AND owner_user_id = $1::uuid THEN 0
           WHEN $2::uuid IS NOT NULL AND owner_group_id = $2::uuid THEN 1
           ELSE 2
         END,
         created_at ASC
       LIMIT 1`,
      [ownerUserId, ownerGroupId]
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapRoutingProfile(result.rows[0]);
  }

  async getRoutingProfileById(id: string): Promise<WorkerRoutingProfile | null> {
    const result = await this.db.query<RoutingProfileRow>(
      `SELECT id, name, owner_user_id, owner_group_id, printer_domain_username, printer_secret_ref, default_route_type,
              thermal_label_patterns, fallback_printer_id, sample_pdf_name, sample_pdf_base64, snippet_base64, match_threshold, visual_rules, classification_routes
       FROM routing_profiles
       WHERE id = $1
       LIMIT 1`,
      [id]
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapRoutingProfile(result.rows[0]);
  }

  async listVisualProfiles(ownerUserId: string | null, ownerGroupId: string | null): Promise<WorkerVisualProfile[]> {
    const result = await this.db.query<VisualProfileRow>(
      `SELECT id, name, owner_user_id, owner_group_id, snippet_base64, match_mode, route_type, printer_id, labels, is_active
       FROM visual_match_profiles
       WHERE is_active = TRUE
         AND (
           (owner_user_id IS NULL AND owner_group_id IS NULL)
           OR ($1::uuid IS NOT NULL AND owner_user_id = $1::uuid)
           OR ($2::uuid IS NOT NULL AND owner_group_id = $2::uuid)
         )
       ORDER BY created_at ASC`,
      [ownerUserId, ownerGroupId]
    );
    return result.rows.map(mapVisualProfile);
  }

  async getActivePrinters(): Promise<WorkerPrinter[]> {
    const result = await this.db.query<PrinterRow>(
      `SELECT id, name, type, target_uri, domain_username, secret_ref, is_active
       FROM printers
       WHERE is_active = TRUE
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapPrinter);
  }

  async getSystemSettings(): Promise<WorkerSystemSettings> {
    const result = await this.db.query<SystemSettingsRow>(
      `SELECT global_smb_domain_username,
              global_smb_secret_ref,
              global_printer_domain_username,
              global_printer_secret_ref,
              worker_poll_interval_ms,
              smtp_enabled,
              smtp_host,
              smtp_port,
              smtp_secure,
              smtp_username,
              smtp_secret_ref,
              smtp_from,
              smtp_to
       FROM system_settings
       WHERE id = TRUE
       LIMIT 1`
    );

    if (!result.rows[0]) {
      return {
        globalSmbDomainUsername: '',
        globalSmbSecretRef: '',
        globalPrinterDomainUsername: '',
        globalPrinterSecretRef: '',
        workerPollIntervalMs: 5000,
        smtpEnabled: false,
        smtpHost: '',
        smtpPort: 25,
        smtpSecure: false,
        smtpUsername: '',
        smtpSecretRef: '',
        smtpFrom: '',
        smtpTo: []
      };
    }

    return mapSystemSettings(result.rows[0]);
  }

  async getUserPrinterAssignment(userId: string): Promise<WorkerUserPrinterAssignment | null> {
    const result = await this.db.query<UserPrinterAssignmentRow>(
      `SELECT a.user_id,
              (array_agg(a.printer_id) FILTER (WHERE p.type = 'A4'))[1] AS a4_printer_id,
              (array_agg(a.printer_id) FILTER (WHERE p.type = 'THERMAL'))[1] AS thermal_printer_id
       FROM user_printer_assignments a
       JOIN printers p ON p.id = a.printer_id
       WHERE a.user_id = $1
       GROUP BY a.user_id
       LIMIT 1`,
      [userId]
    );

    if (!result.rows[0]) {
      return null;
    }

    return {
      userId: result.rows[0].user_id,
      a4PrinterId: result.rows[0].a4_printer_id,
      thermalPrinterId: result.rows[0].thermal_printer_id
    };
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
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<PrintJobRecord> {
    const result = await this.db.query<PrintJobRow>(
      `INSERT INTO print_jobs(source_id, source_file_id, file_path, file_checksum_sha256, file_mtime, is_cancelled, status, error_message)
       VALUES ($1, $2, $3, $4, $5, FALSE, 'PENDING', NULL)
       RETURNING id, source_id, source_file_id, file_path, file_checksum_sha256, file_mtime, is_cancelled, status, error_message`,
      [input.sourceId, input.sourceFileId, input.filePath, input.checksumSha256, input.fileMtime]
    );

    const row = result.rows[0];

    return {
      id: row.id,
      sourceId: row.source_id ?? input.sourceId,
      sourceFileId: row.source_file_id,
      filePath: row.file_path,
      checksumSha256: row.file_checksum_sha256,
      fileMtime: row.file_mtime,
      isCancelled: row.is_cancelled,
      status:
        row.status === 'FAILURE'
          ? 'FAILURE'
          : row.status === 'SUCCESS'
            ? 'SUCCESS'
            : row.status === 'CANCELLED'
              ? 'CANCELLED'
              : 'PENDING',
      errorMessage: row.error_message
    };
  }

  async getPrintJob(jobId: string): Promise<PrintJobRecord | null> {
    const result = await this.db.query<PrintJobRow>(
      `SELECT id, source_id, source_file_id, file_path, file_checksum_sha256, file_mtime, is_cancelled, status, error_message
       FROM print_jobs
       WHERE id = $1
       LIMIT 1`,
      [jobId]
    );

    const row = result.rows[0];
    if (!row) {
      return null;
    }

    return {
      id: row.id,
      sourceId: row.source_id ?? '',
      sourceFileId: row.source_file_id,
      filePath: row.file_path,
      checksumSha256: row.file_checksum_sha256,
      fileMtime: row.file_mtime,
      isCancelled: row.is_cancelled,
      status:
        row.status === 'FAILURE'
          ? 'FAILURE'
          : row.status === 'SUCCESS'
            ? 'SUCCESS'
            : row.status === 'CANCELLED'
              ? 'CANCELLED'
              : 'PENDING',
      errorMessage: row.error_message
    };
  }

  async listPrintJobs(limit = 100): Promise<PrintJobRecord[]> {
    const result = await this.db.query<PrintJobRow>(
      `SELECT id, source_id, source_file_id, file_path, file_checksum_sha256, file_mtime, is_cancelled, status, error_message
       FROM print_jobs
       ORDER BY created_at DESC, id DESC
       LIMIT $1`,
      [limit]
    );

    return result.rows.map((row) => ({
      id: row.id,
      sourceId: row.source_id ?? '',
      sourceFileId: row.source_file_id,
      filePath: row.file_path,
      checksumSha256: row.file_checksum_sha256,
      fileMtime: row.file_mtime,
      isCancelled: row.is_cancelled,
      status:
        row.status === 'FAILURE'
          ? 'FAILURE'
          : row.status === 'SUCCESS'
            ? 'SUCCESS'
            : row.status === 'CANCELLED'
              ? 'CANCELLED'
              : 'PENDING',
      errorMessage: row.error_message
    }));
  }

  async listPrintJobPages(jobId: string): Promise<PrintJobPageRecord[]> {
    const result = await this.db.query<{
      print_job_id: string;
      page_number: number;
      route_type: 'A4' | 'THERMAL';
      printer_id: string | null;
      status: 'SUCCESS' | 'FAILURE' | 'SKIPPED';
      error_message: string | null;
    }>(
      `SELECT print_job_id, page_number, route_type, printer_id, status, error_message
       FROM print_job_pages
       WHERE print_job_id = $1
       ORDER BY page_number ASC, id ASC`,
      [jobId]
    );

    return result.rows.map((row) => ({
      printJobId: row.print_job_id,
      pageNumber: row.page_number,
      routeType: row.route_type,
      printerId: row.printer_id,
      status: row.status,
      errorMessage: row.error_message ?? undefined
    }));
  }

  async cancelPrintJob(jobId: string): Promise<PrintJobRecord | null> {
    const result = await this.db.query<PrintJobRow>(
      `UPDATE print_jobs
       SET is_cancelled = TRUE,
           status = 'CANCELLED',
           updated_at = NOW()
       WHERE id = $1
       RETURNING id, source_id, source_file_id, file_path, file_checksum_sha256, file_mtime, is_cancelled, status, error_message`,
      [jobId]
    );

    const row = result.rows[0];
    if (!row) {
      return null;
    }

    return {
      id: row.id,
      sourceId: row.source_id ?? '',
      sourceFileId: row.source_file_id,
      filePath: row.file_path,
      checksumSha256: row.file_checksum_sha256,
      fileMtime: row.file_mtime,
      isCancelled: row.is_cancelled,
      status: 'CANCELLED',
      errorMessage: row.error_message
    };
  }

  async retryPrintJob(jobId: string): Promise<PrintJobRecord | null> {
    const result = await this.db.query<PrintJobRow>(
      `UPDATE print_jobs
       SET is_cancelled = FALSE,
           status = CASE WHEN status = 'CANCELLED' THEN 'FAILURE' ELSE status END,
           updated_at = NOW()
       WHERE id = $1
       RETURNING id, source_id, source_file_id, file_path, file_checksum_sha256, file_mtime, is_cancelled, status, error_message`,
      [jobId]
    );

    const row = result.rows[0];
    if (!row) {
      return null;
    }

    return {
      id: row.id,
      sourceId: row.source_id ?? '',
      sourceFileId: row.source_file_id,
      filePath: row.file_path,
      checksumSha256: row.file_checksum_sha256,
      fileMtime: row.file_mtime,
      isCancelled: row.is_cancelled,
      status:
        row.status === 'SUCCESS' ? 'SUCCESS' : row.status === 'FAILURE' ? 'FAILURE' : row.status === 'CANCELLED' ? 'CANCELLED' : 'PENDING',
      errorMessage: row.error_message
    };
  }

  async isFileCancelled(input: {
    sourceId: string;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<boolean> {
    const result = await this.db.query<{ is_cancelled: boolean }>(
      `SELECT is_cancelled
       FROM print_jobs
       WHERE source_id = $1
         AND file_path = $2
         AND file_checksum_sha256 = $3
         AND ((file_mtime IS NULL AND $4::timestamptz IS NULL) OR file_mtime = $4)
       ORDER BY created_at DESC, id DESC
       LIMIT 1`,
      [input.sourceId, input.filePath, input.checksumSha256, input.fileMtime]
    );

    return Boolean(result.rows[0]?.is_cancelled);
  }

  async listSuccessfulPageDispatches(input: {
    sourceId: string;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<SuccessfulPageDispatchRecord[]> {
    const result = await this.db.query<{ page_number: number; route_type: 'A4' | 'THERMAL' }>(
      `SELECT DISTINCT p.page_number, p.route_type
       FROM print_job_pages p
       JOIN print_jobs j ON j.id = p.print_job_id
       WHERE j.source_id = $1
         AND j.file_path = $2
         AND j.file_checksum_sha256 = $3
         AND ((j.file_mtime IS NULL AND $4::timestamptz IS NULL) OR j.file_mtime = $4)
         AND p.status = 'SUCCESS'
       ORDER BY p.page_number ASC`,
      [input.sourceId, input.filePath, input.checksumSha256, input.fileMtime]
    );

    return result.rows.map((row) => ({
      pageNumber: row.page_number,
      routeType: row.route_type
    }));
  }

  async addPrintJobPage(input: PrintJobPageRecord): Promise<void> {
    await this.db.query(
      `INSERT INTO print_job_pages(print_job_id, page_number, route_type, printer_id, status, error_message,
                                   page_class, classification_confidence, carrier)
       VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)`,
      [
        input.printJobId,
        input.pageNumber,
        input.routeType,
        input.printerId,
        input.status,
        input.errorMessage ?? null,
        input.pageClass ?? null,
        input.classificationConfidence ?? null,
        input.carrier ?? null
      ]
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
