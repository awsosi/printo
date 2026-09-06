import { createHash, randomBytes } from 'node:crypto';
import type { Pool } from 'pg';
import type { JsonObject } from '../types.js';
import type {
  AccountingReconciliation,
  AccountingRow,
  AgentDecisionMode,
  AgentJobArtifactRecord,
  AgentJobPageRecord,
  AgentJobPageInput,
  AgentJobRecord,
  AgentPrinterRecord,
  AgentRecord,
  FallbackEventInput,
  FallbackEventRecord,
  FallbackSummaryRow,
  ReviewQueueRecord,
  RetentionPolicyRecord,
  RoutingRuleSetRecord,
  RuleBundleRecord
} from './types.js';

/**
 * Persistence for the Windows agent fleet.
 *
 * Kept behind an interface for the same reason `AuthStore` is: the route handlers are then
 * testable without a database, and the compose smoke path can assert against real Postgres.
 */
export interface AgentStore {
  /** Consumes an enrolment token and registers the machine. */
  enroll(input: {
    token: string;
    machineName: string;
    installId: string;
    osVersion?: string | null;
    agentVersion?: string | null;
  }): Promise<{ agent: AgentRecord; apiKey: string } | null>;

  createEnrollmentToken(input: {
    label?: string | null;
    expiresAt: Date;
    maxUses?: number;
    createdBy?: string | null;
  }): Promise<{ id: string; token: string; expiresAt: string }>;

  /** Resolves an agent from its API key. */
  authenticate(apiKey: string): Promise<AgentRecord | null>;

  listAgents(): Promise<AgentRecord[]>;
  getAgent(agentId: string): Promise<AgentRecord | null>;

  /**
   * Changes what an administrator controls about an agent.
   *
   * `status` is the one that matters operationally: setting a stolen or re-imaged machine to
   * DISABLED cuts it off on its next request without touching anyone's login, which is the
   * whole reason agents authenticate with their own credential.
   */
  updateAgent(
    agentId: string,
    changes: {
      decisionMode?: AgentDecisionMode;
      confidenceThreshold?: number;
      status?: AgentRecord['status'];
    }
  ): Promise<AgentRecord | null>;

  heartbeat(input: {
    agentId: string;
    agentVersion?: string | null;
    osVersion?: string | null;
    lastUser?: string | null;
    bundleVersion?: number | null;
  }): Promise<void>;

  replacePrinters(agentId: string, printers: Array<Omit<AgentPrinterRecord, 'id' | 'agentId' | 'reportedAt'>>): Promise<AgentPrinterRecord[]>;
  listPrinters(agentId: string): Promise<AgentPrinterRecord[]>;

  /** The newest published bundle, or `null` when nothing has been published. */
  latestBundle(): Promise<RuleBundleRecord | null>;
  publishBundle(input: { payload: JsonObject; notes?: string | null; publishedBy?: string | null }): Promise<RuleBundleRecord>;

  listRuleSets(): Promise<RoutingRuleSetRecord[]>;
  saveRuleSet(input: { name: string; rules: JsonObject; createdBy?: string | null }): Promise<RoutingRuleSetRecord>;

  /** Records a job, or returns the existing one for the same key. */
  recordJob(input: {
    agentId: string;
    jobKey: string;
    source: AgentJobRecord['source'];
    sourceDetail?: string | null;
    fileName: string;
    documentSha256: string;
    pageCount?: number;
    userName?: string | null;
    status: string;
    bundleVersion?: number | null;
    error?: string | null;
  }): Promise<AgentJobRecord>;

  recordPages(agentJobId: string, pages: AgentJobPageInput[]): Promise<void>;

  recordEvent(input: {
    agentJobId: string;
    level: 'info' | 'warning' | 'error';
    code: string;
    detail?: JsonObject | null;
  }): Promise<void>;

  recordFallback(agentJobId: string, event: FallbackEventInput): Promise<FallbackEventRecord>;

  listJobs(options?: { agentId?: string; limit?: number }): Promise<AgentJobRecord[]>;

  /** One job with its per-page outcomes, for "why did this page print like that". */
  getJob(agentJobId: string): Promise<{ job: AgentJobRecord; pages: AgentJobPageRecord[] } | null>;

  /** Stores a rendered page image, replacing any previous one for that page. */
  saveArtifact(input: {
    agentJobId: string;
    pageNumber: number;
    contentType: string;
    bytes: Buffer;
  }): Promise<void>;

  /** Page numbers that have an image, so the console knows what it can show. */
  listArtifacts(agentJobId: string): Promise<number[]>;

  getArtifact(agentJobId: string, pageNumber: number): Promise<AgentJobArtifactRecord | null>;
  listFallbacks(options?: { limit?: number; reasonCode?: string }): Promise<FallbackEventRecord[]>;
  summariseFallbacks(): Promise<FallbackSummaryRow[]>;

  listReviewQueue(status?: ReviewQueueRecord['status']): Promise<ReviewQueueRecord[]>;

  /** Attaches a derived rule to a review item without changing its status. */
  setProposedRule(id: string, rule: JsonObject): Promise<ReviewQueueRecord | null>;

  /** The review item and the fallback it came from, which carries the engine's trace. */
  getReviewItem(id: string): Promise<{
    item: ReviewQueueRecord;
    fallback: FallbackEventRecord | null;
  } | null>;
  resolveReviewItem(input: {
    id: string;
    status: 'RESOLVED' | 'DISMISSED';
    resolution?: string | null;
    proposedRule?: JsonObject | null;
    resolvedBy?: string | null;
  }): Promise<ReviewQueueRecord | null>;

  /** What was printed in a window, grouped by machine, user and destination. */
  summariseAccounting(range: { from: Date; to: Date }): Promise<{
    rows: AccountingRow[];
    reconciliation: AccountingReconciliation;
  }>;

  listRetentionPolicies(): Promise<RetentionPolicyRecord[]>;
  setRetentionPolicy(scope: string, retainDays: number): Promise<RetentionPolicyRecord | null>;
  /** Deletes data past its retention window. Returns rows removed per scope. */
  applyRetention(now?: Date): Promise<Record<string, number>>;
}

/** Secrets are stored hashed; the plaintext exists only on the workstation. */
function hashSecret(value: string): string {
  return createHash('sha256').update(value, 'utf8').digest('hex');
}

function newSecret(): string {
  return randomBytes(32).toString('base64url');
}

function toIso(value: Date | string | null): string | null {
  if (value === null) {
    return null;
  }
  return value instanceof Date ? value.toISOString() : new Date(value).toISOString();
}

export class PostgresAgentStore implements AgentStore {
  constructor(private readonly pool: Pool) {}

  async createEnrollmentToken(input: {
    label?: string | null;
    expiresAt: Date;
    maxUses?: number;
    createdBy?: string | null;
  }): Promise<{ id: string; token: string; expiresAt: string }> {
    const token = newSecret();
    const result = await this.pool.query(
      `INSERT INTO agent_enrollment_tokens (token_hash, label, expires_at, max_uses, created_by)
       VALUES ($1, $2, $3, $4, $5)
       RETURNING id, expires_at`,
      [hashSecret(token), input.label ?? null, input.expiresAt, input.maxUses ?? 1, input.createdBy ?? null]
    );

    return {
      id: result.rows[0].id,
      // Returned once, here, and never retrievable again — the row keeps only the hash.
      token,
      expiresAt: toIso(result.rows[0].expires_at)!
    };
  }

  async enroll(input: {
    token: string;
    machineName: string;
    installId: string;
    osVersion?: string | null;
    agentVersion?: string | null;
  }): Promise<{ agent: AgentRecord; apiKey: string } | null> {
    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');

      // Claim the token inside the transaction: two machines enrolling with the same
      // single-use token must not both succeed.
      const claimed = await client.query(
        `UPDATE agent_enrollment_tokens
            SET used_count = used_count + 1
          WHERE token_hash = $1
            AND revoked_at IS NULL
            AND expires_at > NOW()
            AND used_count < max_uses
          RETURNING id`,
        [hashSecret(input.token)]
      );

      if (claimed.rowCount === 0) {
        await client.query('ROLLBACK');
        return null;
      }

      const apiKey = newSecret();

      // Re-enrolling the same install replaces its key rather than creating a duplicate:
      // a machine that was re-imaged and re-enrolled is still one workstation.
      const agent = await client.query(
        `INSERT INTO agents (machine_name, install_id, os_version, agent_version, api_key_hash)
         VALUES ($1, $2, $3, $4, $5)
         ON CONFLICT (install_id) DO UPDATE
           SET machine_name = EXCLUDED.machine_name,
               os_version = EXCLUDED.os_version,
               agent_version = EXCLUDED.agent_version,
               api_key_hash = EXCLUDED.api_key_hash,
               status = 'ACTIVE',
               updated_at = NOW()
         RETURNING *`,
        [
          input.machineName,
          input.installId,
          input.osVersion ?? null,
          input.agentVersion ?? null,
          hashSecret(apiKey)
        ]
      );

      await client.query('COMMIT');
      return { agent: mapAgent(agent.rows[0]), apiKey };
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async authenticate(apiKey: string): Promise<AgentRecord | null> {
    const result = await this.pool.query(
      `SELECT * FROM agents WHERE api_key_hash = $1 AND status = 'ACTIVE'`,
      [hashSecret(apiKey)]
    );
    return result.rowCount ? mapAgent(result.rows[0]) : null;
  }

  async listAgents(): Promise<AgentRecord[]> {
    const result = await this.pool.query('SELECT * FROM agents ORDER BY machine_name');
    return result.rows.map(mapAgent);
  }

  async getAgent(agentId: string): Promise<AgentRecord | null> {
    const result = await this.pool.query('SELECT * FROM agents WHERE id = $1', [agentId]);
    return result.rowCount ? mapAgent(result.rows[0]) : null;
  }

  async heartbeat(input: {
    agentId: string;
    agentVersion?: string | null;
    osVersion?: string | null;
    lastUser?: string | null;
    bundleVersion?: number | null;
  }): Promise<void> {
    await this.pool.query(
      `UPDATE agents
          SET last_seen_at = NOW(),
              agent_version = COALESCE($2, agent_version),
              os_version = COALESCE($3, os_version),
              last_user = COALESCE($4, last_user),
              bundle_version = COALESCE($5, bundle_version),
              updated_at = NOW()
        WHERE id = $1`,
      [
        input.agentId,
        input.agentVersion ?? null,
        input.osVersion ?? null,
        input.lastUser ?? null,
        input.bundleVersion ?? null
      ]
    );
  }

  async replacePrinters(
    agentId: string,
    printers: Array<Omit<AgentPrinterRecord, 'id' | 'agentId' | 'reportedAt'>>
  ): Promise<AgentPrinterRecord[]> {
    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');

      // Replaced wholesale rather than merged: a printer removed from the workstation must
      // disappear here too, or the admin UI shows a queue that no longer exists.
      await client.query('DELETE FROM agent_printers WHERE agent_id = $1', [agentId]);

      for (const printer of printers) {
        await client.query(
          `INSERT INTO agent_printers
             (agent_id, queue_name, driver_name, port_name, role, alias, media, dpi,
              offset_x_mm, offset_y_mm, zoom_percent, darkness, speed, raw_zpl, capabilities)
           VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)`,
          [
            agentId,
            printer.queueName,
            printer.driverName ?? null,
            printer.portName ?? null,
            printer.role,
            printer.alias ?? null,
            printer.media ?? null,
            printer.dpi ?? null,
            printer.offsetXMm ?? 0,
            printer.offsetYMm ?? 0,
            printer.zoomPercent ?? null,
            printer.darkness ?? null,
            printer.speed ?? null,
            printer.rawZpl ?? false,
            JSON.stringify(printer.capabilities ?? {})
          ]
        );
      }

      await client.query('COMMIT');
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }

    return this.listPrinters(agentId);
  }

  async listPrinters(agentId: string): Promise<AgentPrinterRecord[]> {
    const result = await this.pool.query(
      'SELECT * FROM agent_printers WHERE agent_id = $1 ORDER BY queue_name',
      [agentId]
    );
    return result.rows.map(mapPrinter);
  }

  async latestBundle(): Promise<RuleBundleRecord | null> {
    const result = await this.pool.query(
      'SELECT * FROM rule_bundles ORDER BY version DESC LIMIT 1'
    );
    return result.rowCount ? mapBundle(result.rows[0]) : null;
  }

  async publishBundle(input: {
    payload: JsonObject;
    notes?: string | null;
    publishedBy?: string | null;
  }): Promise<RuleBundleRecord> {
    const serialized = JSON.stringify(input.payload);
    const checksum = createHash('sha256').update(serialized, 'utf8').digest('hex');

    const result = await this.pool.query(
      `INSERT INTO rule_bundles (version, payload, checksum, published_by, notes)
       VALUES (
         (SELECT COALESCE(MAX(version), 0) + 1 FROM rule_bundles),
         $1, $2, $3, $4)
       RETURNING *`,
      [serialized, checksum, input.publishedBy ?? null, input.notes ?? null]
    );

    return mapBundle(result.rows[0]);
  }

  async updateAgent(
    agentId: string,
    changes: {
      decisionMode?: AgentDecisionMode;
      confidenceThreshold?: number;
      status?: AgentRecord['status'];
    }
  ): Promise<AgentRecord | null> {
    // COALESCE rather than a built statement: an omitted field must keep its stored value,
    // and a partial update that silently reset the other two would be a trap.
    const result = await this.pool.query(
      `UPDATE agents
          SET decision_mode = COALESCE($2, decision_mode),
              confidence_threshold = COALESCE($3, confidence_threshold),
              status = COALESCE($4, status),
              updated_at = NOW()
        WHERE id = $1
      RETURNING *`,
      [
        agentId,
        changes.decisionMode ?? null,
        changes.confidenceThreshold ?? null,
        changes.status ?? null
      ]
    );

    return result.rowCount ? mapAgent(result.rows[0]) : null;
  }

  async listRuleSets(): Promise<RoutingRuleSetRecord[]> {
    const result = await this.pool.query(
      'SELECT * FROM routing_rule_sets ORDER BY name, version DESC'
    );
    return result.rows.map(mapRuleSet);
  }

  async saveRuleSet(input: {
    name: string;
    rules: JsonObject;
    createdBy?: string | null;
  }): Promise<RoutingRuleSetRecord> {
    const result = await this.pool.query(
      `INSERT INTO routing_rule_sets (name, rules, version, created_by)
       VALUES ($1, $2,
         (SELECT COALESCE(MAX(version), 0) + 1 FROM routing_rule_sets WHERE name = $1),
         $3)
       RETURNING *`,
      [input.name, JSON.stringify(input.rules), input.createdBy ?? null]
    );
    return mapRuleSet(result.rows[0]);
  }

  async recordJob(input: {
    agentId: string;
    jobKey: string;
    source: AgentJobRecord['source'];
    sourceDetail?: string | null;
    fileName: string;
    documentSha256: string;
    pageCount?: number;
    userName?: string | null;
    status: string;
    bundleVersion?: number | null;
    error?: string | null;
  }): Promise<AgentJobRecord> {
    const result = await this.pool.query(
      `INSERT INTO agent_jobs
         (agent_id, job_key, source, source_detail, file_name, doc_sha256,
          page_count, user_name, status, bundle_version, error)
       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
       ON CONFLICT (agent_id, job_key) DO UPDATE
         SET status = EXCLUDED.status,
             page_count = GREATEST(agent_jobs.page_count, EXCLUDED.page_count),
             error = EXCLUDED.error,
             updated_at = NOW()
       RETURNING *`,
      [
        input.agentId,
        input.jobKey,
        input.source,
        input.sourceDetail ?? null,
        input.fileName,
        input.documentSha256,
        input.pageCount ?? 0,
        input.userName ?? null,
        input.status,
        input.bundleVersion ?? null,
        input.error ?? null
      ]
    );
    return mapJob(result.rows[0]);
  }

  async recordPages(agentJobId: string, pages: AgentJobPageInput[]): Promise<void> {
    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');

      for (const page of pages) {
        const inserted = await client.query(
          `INSERT INTO agent_job_pages
             (agent_job_id, page_number, page_class, carrier, confidence, rule_id, route,
              printer_queue, transform)
           VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
           ON CONFLICT (agent_job_id, page_number) DO UPDATE
             SET page_class = EXCLUDED.page_class,
                 carrier = EXCLUDED.carrier,
                 confidence = EXCLUDED.confidence,
                 rule_id = EXCLUDED.rule_id,
                 route = EXCLUDED.route,
                 printer_queue = EXCLUDED.printer_queue,
                 transform = EXCLUDED.transform
           RETURNING id`,
          [
            agentJobId,
            page.pageNumber,
            page.pageClass ?? null,
            page.carrier ?? null,
            page.confidence ?? null,
            page.ruleId ?? null,
            page.route ?? null,
            page.printerQueue ?? null,
            page.transform ? JSON.stringify(page.transform) : null
          ]
        );

        const pageId = inserted.rows[0].id;
        await client.query('DELETE FROM agent_job_page_traces WHERE agent_job_page_id = $1', [pageId]);

        for (const trace of page.traces ?? []) {
          await client.query(
            `INSERT INTO agent_job_page_traces
               (agent_job_page_id, rule_id, outcome, failed_predicate, measured)
             VALUES ($1,$2,$3,$4,$5)`,
            [
              pageId,
              trace.ruleId,
              trace.outcome,
              trace.failedPredicate ?? null,
              trace.measured ? JSON.stringify(trace.measured) : null
            ]
          );
        }
      }

      await client.query('COMMIT');
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async recordEvent(input: {
    agentJobId: string;
    level: 'info' | 'warning' | 'error';
    code: string;
    detail?: JsonObject | null;
  }): Promise<void> {
    await this.pool.query(
      `INSERT INTO agent_job_events (agent_job_id, level, code, detail)
       VALUES ($1,$2,$3,$4)`,
      [input.agentJobId, input.level, input.code, input.detail ? JSON.stringify(input.detail) : null]
    );
  }

  async recordFallback(agentJobId: string, event: FallbackEventInput): Promise<FallbackEventRecord> {
    const result = await this.pool.query(
      `INSERT INTO fallback_events
         (agent_job_id, reason_code, message, engine_selection, user_selection,
          resolution, decision_ms, trace, thumbnails_ref, resolved_at)
       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
       RETURNING *`,
      [
        agentJobId,
        event.reasonCode,
        event.message ?? null,
        event.engineSelection,
        event.userSelection ?? null,
        event.resolution ?? null,
        event.decisionMs ?? null,
        event.trace ? JSON.stringify(event.trace) : null,
        event.thumbnailsRef ?? null,
        event.resolution && event.resolution !== 'unanswered' ? new Date() : null
      ]
    );

    const record = mapFallback(result.rows[0]);

    // Every answered fallback becomes a review item. That is the mechanism the plan calls for:
    // a logged fallback an admin can turn into a rule in one click.
    if (event.resolution && event.resolution !== 'unanswered') {
      await this.pool.query(
        `INSERT INTO review_queue (fallback_event_id, reason)
         VALUES ($1, $2)`,
        [record.id, event.reasonCode]
      );
    }

    return record;
  }

  async listJobs(options?: { agentId?: string; limit?: number }): Promise<AgentJobRecord[]> {
    const limit = Math.min(Math.max(options?.limit ?? 100, 1), 500);
    const result = options?.agentId
      ? await this.pool.query(
          'SELECT * FROM agent_jobs WHERE agent_id = $1 ORDER BY created_at DESC LIMIT $2',
          [options.agentId, limit]
        )
      : await this.pool.query('SELECT * FROM agent_jobs ORDER BY created_at DESC LIMIT $1', [limit]);
    return result.rows.map(mapJob);
  }

  async getJob(agentJobId: string): Promise<{ job: AgentJobRecord; pages: AgentJobPageRecord[] } | null> {
    const found = await this.pool.query('SELECT * FROM agent_jobs WHERE id = $1', [agentJobId]);
    if (!found.rowCount) {
      return null;
    }

    const pages = await this.pool.query(
      'SELECT * FROM agent_job_pages WHERE agent_job_id = $1 ORDER BY page_number',
      [agentJobId]
    );

    return {
      job: mapJob(found.rows[0]),
      pages: pages.rows.map((row) => ({
        id: row.id as string,
        agentJobId: row.agent_job_id as string,
        pageNumber: row.page_number as number,
        pageClass: (row.page_class as string) ?? null,
        carrier: (row.carrier as string) ?? null,
        confidence: row.confidence === null ? null : Number(row.confidence),
        ruleId: (row.rule_id as string) ?? null,
        route: (row.route as string) ?? null,
        printerQueue: (row.printer_queue as string) ?? null,
        transform: (row.transform as JsonObject) ?? null
      }))
    };
  }

  async saveArtifact(input: {
    agentJobId: string;
    pageNumber: number;
    contentType: string;
    bytes: Buffer;
  }): Promise<void> {
    // Replace rather than accumulate: a job re-reported after a retry must not leave two
    // images of the same page, and the newer render is the one that matches the decision.
    await this.pool.query(
      `INSERT INTO agent_job_artifacts (agent_job_id, page_number, kind, content_type, bytes)
       VALUES ($1, $2, 'thumbnail', $3, $4)
       ON CONFLICT (agent_job_id, page_number, kind)
       DO UPDATE SET content_type = EXCLUDED.content_type,
                     bytes = EXCLUDED.bytes,
                     created_at = NOW()`,
      [input.agentJobId, input.pageNumber, input.contentType, input.bytes]
    );
  }

  async listArtifacts(agentJobId: string): Promise<number[]> {
    const result = await this.pool.query(
      'SELECT page_number FROM agent_job_artifacts WHERE agent_job_id = $1 ORDER BY page_number',
      [agentJobId]
    );
    return result.rows.map((row) => row.page_number as number);
  }

  async getArtifact(agentJobId: string, pageNumber: number): Promise<AgentJobArtifactRecord | null> {
    const result = await this.pool.query(
      `SELECT * FROM agent_job_artifacts
        WHERE agent_job_id = $1 AND page_number = $2 AND kind = 'thumbnail'`,
      [agentJobId, pageNumber]
    );

    if (!result.rowCount) {
      return null;
    }

    const row = result.rows[0];
    return {
      id: row.id as string,
      agentJobId: row.agent_job_id as string,
      pageNumber: row.page_number as number,
      kind: 'thumbnail',
      contentType: row.content_type as string,
      bytes: row.bytes as Buffer,
      createdAt: toIso(row.created_at as Date)!
    };
  }

  async listFallbacks(options?: { limit?: number; reasonCode?: string }): Promise<FallbackEventRecord[]> {
    const limit = Math.min(Math.max(options?.limit ?? 100, 1), 500);
    const result = options?.reasonCode
      ? await this.pool.query(
          'SELECT * FROM fallback_events WHERE reason_code = $1 ORDER BY raised_at DESC LIMIT $2',
          [options.reasonCode, limit]
        )
      : await this.pool.query('SELECT * FROM fallback_events ORDER BY raised_at DESC LIMIT $1', [limit]);
    return result.rows.map(mapFallback);
  }

  async summariseFallbacks(): Promise<FallbackSummaryRow[]> {
    // Grouped by reason, with how often the user simply confirmed what the engine proposed.
    // A reason with a high agreement rate is a threshold that is set too tight; one with a low
    // rate is a rule that is actually wrong. Telling those apart is the point of the view.
    const result = await this.pool.query(
      `SELECT reason_code,
              COUNT(*)::int AS total,
              COUNT(*) FILTER (WHERE resolution IS NOT NULL AND resolution <> 'unanswered')::int AS answered,
              COUNT(*) FILTER (WHERE user_selection IS NOT NULL AND user_selection = engine_selection)::int AS agreed,
              PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY decision_ms) AS median_ms
         FROM fallback_events
        GROUP BY reason_code
        ORDER BY total DESC`
    );

    return result.rows.map((row) => ({
      reasonCode: row.reason_code,
      total: row.total,
      answered: row.answered,
      agreedWithEngine: row.agreed,
      medianDecisionMs: row.median_ms === null ? null : Number(row.median_ms)
    }));
  }

  async listReviewQueue(status?: ReviewQueueRecord['status']): Promise<ReviewQueueRecord[]> {
    const result = status
      ? await this.pool.query(
          'SELECT * FROM review_queue WHERE status = $1 ORDER BY created_at DESC LIMIT 200',
          [status]
        )
      : await this.pool.query('SELECT * FROM review_queue ORDER BY created_at DESC LIMIT 200');
    return result.rows.map(mapReview);
  }

  async getReviewItem(id: string): Promise<{
    item: ReviewQueueRecord;
    fallback: FallbackEventRecord | null;
  } | null> {
    const found = await this.pool.query('SELECT * FROM review_queue WHERE id = $1', [id]);
    if (!found.rowCount) {
      return null;
    }

    const item = mapReview(found.rows[0]);
    if (!item.fallbackEventId) {
      return { item, fallback: null };
    }

    const fallback = await this.pool.query('SELECT * FROM fallback_events WHERE id = $1', [
      item.fallbackEventId
    ]);

    return { item, fallback: fallback.rowCount ? mapFallback(fallback.rows[0]) : null };
  }

  async setProposedRule(id: string, rule: JsonObject): Promise<ReviewQueueRecord | null> {
    // Status is untouched on purpose: a proposal is something to read, not a decision. The
    // item stays open until a person says what they did about it.
    const result = await this.pool.query(
      'UPDATE review_queue SET proposed_rule = $2 WHERE id = $1 RETURNING *',
      [id, JSON.stringify(rule)]
    );
    return result.rowCount ? mapReview(result.rows[0]) : null;
  }

  async resolveReviewItem(input: {
    id: string;
    status: 'RESOLVED' | 'DISMISSED';
    resolution?: string | null;
    proposedRule?: JsonObject | null;
    resolvedBy?: string | null;
  }): Promise<ReviewQueueRecord | null> {
    const result = await this.pool.query(
      `UPDATE review_queue
          SET status = $2,
              resolution = $3,
              proposed_rule = $4,
              resolved_by = $5,
              resolved_at = NOW()
        WHERE id = $1
        RETURNING *`,
      [
        input.id,
        input.status,
        input.resolution ?? null,
        input.proposedRule ? JSON.stringify(input.proposedRule) : null,
        input.resolvedBy ?? null
      ]
    );
    return result.rowCount ? mapReview(result.rows[0]) : null;
  }

  async summariseAccounting(range: { from: Date; to: Date }): Promise<{
    rows: AccountingRow[];
    reconciliation: AccountingReconciliation;
  }> {
    // LEFT JOIN, not INNER: a job that failed before any page was routed still belongs in the
    // view. Dropping it would make a machine that is failing every job look idle.
    const rows = await this.pool.query(
      `SELECT a.id                AS agent_id,
              a.machine_name      AS machine_name,
              j.user_name         AS user_name,
              p.route             AS route,
              p.printer_queue     AS printer_queue,
              COUNT(DISTINCT j.id)::int AS jobs,
              COUNT(p.id)::int          AS pages
         FROM agent_jobs j
         JOIN agents a ON a.id = j.agent_id
    LEFT JOIN agent_job_pages p ON p.agent_job_id = j.id
        WHERE j.created_at >= $1 AND j.created_at < $2
     GROUP BY a.id, a.machine_name, j.user_name, p.route, p.printer_queue
     ORDER BY pages DESC, machine_name`,
      [range.from, range.to]
    );

    // Reconciliation covers completed jobs only. An failed or awaiting-user job legitimately
    // has fewer page rows than pages, and counting those as discrepancies would bury the real
    // ones in noise.
    const totals = await this.pool.query(
      `SELECT COUNT(*)::int                    AS completed_jobs,
              COALESCE(SUM(j.page_count), 0)::int AS declared_pages,
              COALESCE(SUM(counted.pages), 0)::int AS recorded_pages
         FROM agent_jobs j
    LEFT JOIN LATERAL (
              SELECT COUNT(*)::int AS pages
                FROM agent_job_pages p
               WHERE p.agent_job_id = j.id
         ) counted ON TRUE
        WHERE j.status = 'COMPLETED'
          AND j.created_at >= $1 AND j.created_at < $2`,
      [range.from, range.to]
    );

    const discrepancies = await this.pool.query(
      `SELECT j.id            AS agent_job_id,
              a.machine_name  AS machine_name,
              j.file_name     AS file_name,
              j.page_count::int AS declared_pages,
              COALESCE(counted.pages, 0)::int AS recorded_pages
         FROM agent_jobs j
         JOIN agents a ON a.id = j.agent_id
    LEFT JOIN LATERAL (
              SELECT COUNT(*)::int AS pages
                FROM agent_job_pages p
               WHERE p.agent_job_id = j.id
         ) counted ON TRUE
        WHERE j.status = 'COMPLETED'
          AND j.created_at >= $1 AND j.created_at < $2
          AND j.page_count <> COALESCE(counted.pages, 0)
     ORDER BY ABS(j.page_count - COALESCE(counted.pages, 0)) DESC
        LIMIT 50`,
      [range.from, range.to]
    );

    return {
      rows: rows.rows.map((row) => ({
        agentId: row.agent_id as string,
        machineName: row.machine_name as string,
        userName: (row.user_name as string) ?? null,
        route: (row.route as string) ?? null,
        printerQueue: (row.printer_queue as string) ?? null,
        jobs: row.jobs as number,
        pages: row.pages as number
      })),
      reconciliation: {
        completedJobs: totals.rows[0].completed_jobs as number,
        declaredPages: totals.rows[0].declared_pages as number,
        recordedPages: totals.rows[0].recorded_pages as number,
        discrepancies: discrepancies.rows.map((row) => ({
          agentJobId: row.agent_job_id as string,
          machineName: row.machine_name as string,
          fileName: row.file_name as string,
          declaredPages: row.declared_pages as number,
          recordedPages: row.recorded_pages as number
        }))
      }
    };
  }

  async listRetentionPolicies(): Promise<RetentionPolicyRecord[]> {
    const result = await this.pool.query('SELECT * FROM retention_policies ORDER BY scope');
    return result.rows.map((row) => ({
      scope: row.scope,
      retainDays: row.retain_days,
      lastRunAt: toIso(row.last_run_at)
    }));
  }

  async setRetentionPolicy(scope: string, retainDays: number): Promise<RetentionPolicyRecord | null> {
    const result = await this.pool.query(
      `UPDATE retention_policies SET retain_days = $2, updated_at = NOW()
        WHERE scope = $1 RETURNING *`,
      [scope, retainDays]
    );
    return result.rowCount
      ? { scope: result.rows[0].scope, retainDays: result.rows[0].retain_days, lastRunAt: toIso(result.rows[0].last_run_at) }
      : null;
  }

  async applyRetention(now: Date = new Date()): Promise<Record<string, number>> {
    const policies = await this.listRetentionPolicies();
    const removed: Record<string, number> = {};

    for (const policy of policies) {
      const cutoff = new Date(now.getTime() - policy.retainDays * 24 * 60 * 60 * 1000);

      // Only the scopes with a table to prune here; documents and thumbnails live in blob
      // storage and are swept by the same schedule from the worker.
      const statement =
        policy.scope === 'traces'
          ? `DELETE FROM agent_job_page_traces
              WHERE agent_job_page_id IN (
                SELECT p.id FROM agent_job_pages p
                  JOIN agent_jobs j ON j.id = p.agent_job_id
                 WHERE j.created_at < $1)`
          : policy.scope === 'thumbnails'
            ? `DELETE FROM agent_job_artifacts WHERE created_at < $1`
          : policy.scope === 'job_history'
            ? 'DELETE FROM agent_jobs WHERE created_at < $1'
            : policy.scope === 'fallbacks'
              ? 'DELETE FROM fallback_events WHERE raised_at < $1'
              : null;

      if (!statement) {
        continue;
      }

      const result = await this.pool.query(statement, [cutoff]);
      removed[policy.scope] = result.rowCount ?? 0;
    }

    await this.pool.query('UPDATE retention_policies SET last_run_at = NOW()');
    return removed;
  }
}

function mapAgent(row: Record<string, unknown>): AgentRecord {
  return {
    id: row.id as string,
    machineName: row.machine_name as string,
    installId: row.install_id as string,
    osVersion: (row.os_version as string) ?? null,
    agentVersion: (row.agent_version as string) ?? null,
    lastUser: (row.last_user as string) ?? null,
    decisionMode: row.decision_mode as AgentDecisionMode,
    confidenceThreshold: Number(row.confidence_threshold),
    bundleVersion: row.bundle_version === null ? null : Number(row.bundle_version),
    status: row.status as AgentRecord['status'],
    enrolledAt: toIso(row.enrolled_at as Date)!,
    lastSeenAt: toIso((row.last_seen_at as Date) ?? null)
  };
}

function mapPrinter(row: Record<string, unknown>): AgentPrinterRecord {
  return {
    id: row.id as string,
    agentId: row.agent_id as string,
    queueName: row.queue_name as string,
    driverName: (row.driver_name as string) ?? null,
    portName: (row.port_name as string) ?? null,
    role: row.role as AgentPrinterRecord['role'],
    alias: (row.alias as string) ?? null,
    media: (row.media as string) ?? null,
    dpi: row.dpi === null ? null : Number(row.dpi),
    offsetXMm: Number(row.offset_x_mm),
    offsetYMm: Number(row.offset_y_mm),
    zoomPercent: row.zoom_percent === null ? null : Number(row.zoom_percent),
    darkness: row.darkness === null ? null : Number(row.darkness),
    speed: row.speed === null ? null : Number(row.speed),
    rawZpl: Boolean(row.raw_zpl),
    capabilities: (row.capabilities as JsonObject) ?? {},
    reportedAt: toIso(row.reported_at as Date)!
  };
}

function mapBundle(row: Record<string, unknown>): RuleBundleRecord {
  return {
    version: Number(row.version),
    payload: row.payload as JsonObject,
    checksum: row.checksum as string,
    publishedAt: toIso(row.published_at as Date)!,
    notes: (row.notes as string) ?? null
  };
}

function mapRuleSet(row: Record<string, unknown>): RoutingRuleSetRecord {
  return {
    id: row.id as string,
    name: row.name as string,
    rules: row.rules as JsonObject,
    version: Number(row.version),
    isPublished: Boolean(row.is_published),
    publishedAt: toIso((row.published_at as Date) ?? null),
    createdAt: toIso(row.created_at as Date)!
  };
}

function mapJob(row: Record<string, unknown>): AgentJobRecord {
  return {
    id: row.id as string,
    agentId: row.agent_id as string,
    jobKey: row.job_key as string,
    source: row.source as AgentJobRecord['source'],
    sourceDetail: (row.source_detail as string) ?? null,
    fileName: row.file_name as string,
    documentSha256: row.doc_sha256 as string,
    pageCount: Number(row.page_count),
    userName: (row.user_name as string) ?? null,
    status: row.status as string,
    bundleVersion: row.bundle_version === null ? null : Number(row.bundle_version),
    error: (row.error as string) ?? null,
    createdAt: toIso(row.created_at as Date)!,
    updatedAt: toIso(row.updated_at as Date)!
  };
}

function mapFallback(row: Record<string, unknown>): FallbackEventRecord {
  return {
    id: row.id as string,
    agentJobId: row.agent_job_id as string,
    reasonCode: row.reason_code as string,
    message: (row.message as string) ?? null,
    engineSelection: (row.engine_selection as number[]) ?? [],
    userSelection: (row.user_selection as number[]) ?? null,
    resolution: (row.resolution as FallbackEventRecord['resolution']) ?? null,
    decisionMs: row.decision_ms === null ? null : Number(row.decision_ms),
    trace: (row.trace as JsonObject) ?? null,
    thumbnailsRef: (row.thumbnails_ref as string) ?? null,
    raisedAt: toIso(row.raised_at as Date)!,
    resolvedAt: toIso((row.resolved_at as Date) ?? null)
  };
}

function mapReview(row: Record<string, unknown>): ReviewQueueRecord {
  return {
    id: row.id as string,
    agentJobPageId: (row.agent_job_page_id as string) ?? null,
    fallbackEventId: (row.fallback_event_id as string) ?? null,
    reason: row.reason as string,
    status: row.status as ReviewQueueRecord['status'],
    resolution: (row.resolution as string) ?? null,
    proposedRule: (row.proposed_rule as JsonObject) ?? null,
    createdAt: toIso(row.created_at as Date)!,
    resolvedAt: toIso((row.resolved_at as Date) ?? null)
  };
}
