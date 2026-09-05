import { Router, type NextFunction, type Request, type Response } from 'express';
import { ROLES } from '@printo/shared';
import {
  BUILTIN_PROFILES,
  evaluateDocument,
  matchProfile,
  parseBundlePayload,
  parseDocumentFeatures,
  WireFormatError,
  type EngineOptions,
  type RuleBundlePayload
} from '@printo/routing-engine';
import { requireAuth, requireRole } from '../middleware/auth.js';
import type { JsonObject } from '../types.js';
import type { AgentStore } from './store.js';
import type {
  AgentJobPageInput,
  AgentPrinterRecord,
  AgentPrinterRole,
  AgentRecord
} from './types.js';

declare global {
  // eslint-disable-next-line @typescript-eslint/no-namespace
  namespace Express {
    interface Request {
      agent?: AgentRecord;
    }
  }
}

const PRINTER_ROLES: AgentPrinterRole[] = ['A4', 'THERMAL', 'ALIAS'];

function isJsonObject(value: unknown): value is JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function parsePageNumbers(value: unknown): number[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return value
    .map((entry) => Number(entry))
    .filter((entry) => Number.isInteger(entry) && entry > 0);
}

/**
 * Routes for the Windows agent fleet.
 *
 * Two audiences with different credentials on purpose. Agents authenticate with a per-machine
 * API key and may only touch their own record; administrators authenticate with the normal
 * JWT and see the fleet. A workstation that is stolen or re-imaged can then be cut off by
 * disabling one agent, without touching anyone's login.
 */
export function createAgentRouter(store: AgentStore): Router {
  const router = Router();

  /**
   * The rules the server itself executes, and the version it would tell an agent to sync.
   *
   * With no bundle published the server falls back to the same built-in profiles the agent
   * ships with, so `server` and `auto` decision modes work on a fresh install rather than
   * failing until somebody remembers to press publish. A bundle that is present but no longer
   * parses is a different matter and is reported, not silently replaced: agents are already
   * running it, and quietly deciding by different rules than the fleet is the worst outcome.
   */
  async function activeRules(): Promise<{ payload: RuleBundlePayload; version: number | null }> {
    const bundle = await store.latestBundle();
    if (!bundle) {
      return {
        payload: { schemaVersion: 1, profiles: BUILTIN_PROFILES },
        version: null
      };
    }

    return { payload: parseBundlePayload(bundle.payload), version: bundle.version };
  }

  /** Resolves the calling agent from its API key. */
  async function requireAgent(req: Request, res: Response, next: NextFunction) {
    const header = req.header('x-printo-agent-key');
    if (!header) {
      return res.status(401).json({ error: 'AGENT_KEY_REQUIRED' });
    }

    const agent = await store.authenticate(header);
    if (!agent) {
      // Deliberately not distinguishing "unknown key" from "disabled agent": a machine that
      // has been cut off should learn nothing from the response.
      return res.status(401).json({ error: 'INVALID_AGENT_KEY' });
    }

    req.agent = agent;
    return next();
  }

  // -------------------------------------------------------------------------------------------
  // Agent-facing
  // -------------------------------------------------------------------------------------------

  router.post('/agents/enroll', async (req, res) => {
    const token = String(req.body?.token ?? '');
    const machineName = String(req.body?.machineName ?? '').trim();
    const installId = String(req.body?.installId ?? '').trim();

    if (!token || !machineName || !installId) {
      return res.status(400).json({ error: 'INVALID_ENROLLMENT' });
    }

    const result = await store.enroll({
      token,
      machineName,
      installId,
      osVersion: req.body?.osVersion ? String(req.body.osVersion) : null,
      agentVersion: req.body?.agentVersion ? String(req.body.agentVersion) : null
    });

    if (!result) {
      return res.status(403).json({ error: 'ENROLLMENT_REJECTED' });
    }

    // The key is returned exactly once. Losing it means re-enrolling, which is the correct
    // cost: the alternative is a key the server can hand back to anyone who asks.
    return res.status(201).json({ agent: result.agent, apiKey: result.apiKey });
  });

  router.get('/agents/me', requireAgent, async (req, res) => {
    const printers = await store.listPrinters(req.agent!.id);
    return res.json({ agent: req.agent, printers });
  });

  router.post('/agents/me/heartbeat', requireAgent, async (req, res) => {
    await store.heartbeat({
      agentId: req.agent!.id,
      agentVersion: req.body?.agentVersion ? String(req.body.agentVersion) : null,
      osVersion: req.body?.osVersion ? String(req.body.osVersion) : null,
      lastUser: req.body?.lastUser ? String(req.body.lastUser) : null,
      bundleVersion:
        req.body?.bundleVersion === undefined || req.body?.bundleVersion === null
          ? null
          : Number(req.body.bundleVersion)
    });

    const bundle = await store.latestBundle();
    return res.json({
      ok: true,
      // Told on every heartbeat rather than polled separately: the agent then needs no timer
      // of its own to notice a republished rule set.
      bundleVersion: bundle?.version ?? null
    });
  });

  router.get('/agents/me/bundle', requireAgent, async (req, res) => {
    const bundle = await store.latestBundle();
    if (!bundle) {
      return res.status(404).json({ error: 'NO_BUNDLE_PUBLISHED' });
    }

    const since = req.query.since === undefined ? null : Number(req.query.since);
    if (since !== null && Number.isFinite(since) && bundle.version <= since) {
      // Nothing new. 304 keeps a fleet of 30 agents from re-downloading an unchanged bundle
      // every poll.
      return res.status(304).end();
    }

    return res.json(bundle);
  });

  /**
   * Decides one document on the server, from features the agent measured.
   *
   * This is what `server` and `auto` decision modes call. Only the *features* cross the
   * network, never the document: a workstation stays the only place a customer's invoice is
   * rendered, and the payload is a few kilobytes rather than a few megabytes.
   *
   * The two-phase OCR protocol survives the round trip unchanged. The server answers
   * `needs-features` with the rectangles a rule wants, the agent - which is the only side
   * holding the pixels - recognises them and posts the enriched features back. As locally,
   * exactly one extra round is allowed: a second `needs-features` is a defect in the rule set,
   * not a question a user could answer, and it is reported as such rather than looped on.
   */
  router.post('/agents/me/decide', requireAgent, async (req, res) => {
    let features;
    try {
      features = parseDocumentFeatures(req.body?.features);
    } catch (error) {
      if (error instanceof WireFormatError) {
        return res.status(400).json({ error: 'INVALID_FEATURES', detail: error.message });
      }
      throw error;
    }

    let rules;
    try {
      rules = await activeRules();
    } catch (error) {
      if (error instanceof WireFormatError) {
        return res.status(500).json({ error: 'BUNDLE_UNREADABLE', detail: error.message });
      }
      throw error;
    }

    const profile = matchProfile(rules.payload.profiles, features);
    if (!profile) {
      // Not an error: it is the NO_PROFILE_MATCH fallback, and the agent must raise it the
      // same way it would have locally.
      return res.json({ status: 'no-profile', bundleVersion: rules.version });
    }

    const options: EngineOptions = rules.payload.carrierSignatures
      ? { carrierSignatures: rules.payload.carrierSignatures }
      : {};

    const secondPass = Boolean(req.body?.secondPass);
    const evaluation = evaluateDocument(profile, features, options);

    if (evaluation.status === 'needs-features') {
      if (secondPass) {
        return res.status(422).json({
          error: 'RULES_ASK_OCR_TWICE',
          detail: 'the rule set asked for OCR twice; the second pass must be decidable',
          bundleVersion: rules.version
        });
      }

      return res.json({
        status: 'needs-features',
        ocr: evaluation.ocr,
        bundleVersion: rules.version
      });
    }

    return res.json({
      status: 'decided',
      decision: evaluation.document,
      bundleVersion: rules.version
    });
  });

  router.post('/agents/me/printers', requireAgent, async (req, res) => {
    if (!Array.isArray(req.body?.printers)) {
      return res.status(400).json({ error: 'INVALID_PRINTERS' });
    }

    const printers: Array<Omit<AgentPrinterRecord, 'id' | 'agentId' | 'reportedAt'>> =
      req.body.printers.map((entry: Record<string, unknown>) => ({
      queueName: String(entry.queueName ?? ''),
      driverName: entry.driverName ? String(entry.driverName) : null,
      portName: entry.portName ? String(entry.portName) : null,
      role: PRINTER_ROLES.includes(entry.role as AgentPrinterRole)
        ? (entry.role as AgentPrinterRole)
        : 'A4',
      alias: entry.alias ? String(entry.alias) : null,
      media: entry.media ? String(entry.media) : null,
      dpi: entry.dpi === undefined || entry.dpi === null ? null : Number(entry.dpi),
      offsetXMm: Number(entry.offsetXMm ?? 0),
      offsetYMm: Number(entry.offsetYMm ?? 0),
      zoomPercent:
        entry.zoomPercent === undefined || entry.zoomPercent === null ? null : Number(entry.zoomPercent),
      darkness: entry.darkness === undefined || entry.darkness === null ? null : Number(entry.darkness),
      speed: entry.speed === undefined || entry.speed === null ? null : Number(entry.speed),
      rawZpl: Boolean(entry.rawZpl),
      capabilities: isJsonObject(entry.capabilities) ? entry.capabilities : {}
      }));

    if (printers.some((printer) => printer.queueName.length === 0)) {
      return res.status(400).json({ error: 'INVALID_PRINTERS' });
    }

    return res.json({ printers: await store.replacePrinters(req.agent!.id, printers) });
  });

  router.post('/agents/me/jobs', requireAgent, async (req, res) => {
    const jobKey = String(req.body?.jobKey ?? '').trim();
    const fileName = String(req.body?.fileName ?? '').trim();
    const documentSha256 = String(req.body?.documentSha256 ?? '').trim();
    const source = req.body?.source;

    if (!jobKey || !fileName || !documentSha256) {
      return res.status(400).json({ error: 'INVALID_JOB' });
    }

    if (source !== 'HotFolder' && source !== 'VirtualPrinter' && source !== 'Reprint') {
      return res.status(400).json({ error: 'INVALID_SOURCE' });
    }

    const job = await store.recordJob({
      agentId: req.agent!.id,
      jobKey,
      source,
      sourceDetail: req.body?.sourceDetail ? String(req.body.sourceDetail) : null,
      fileName,
      documentSha256,
      pageCount: Number(req.body?.pageCount ?? 0),
      userName: req.body?.userName ? String(req.body.userName) : null,
      status: String(req.body?.status ?? 'PENDING'),
      bundleVersion:
        req.body?.bundleVersion === undefined || req.body?.bundleVersion === null
          ? null
          : Number(req.body.bundleVersion),
      error: req.body?.error ? String(req.body.error) : null
    });

    if (Array.isArray(req.body?.pages) && req.body.pages.length > 0) {
      await store.recordPages(job.id, req.body.pages as AgentJobPageInput[]);
    }

    if (isJsonObject(req.body?.fallback)) {
      const fallback = req.body.fallback;
      await store.recordFallback(job.id, {
        reasonCode: String(fallback.reasonCode ?? 'UNKNOWN'),
        message: fallback.message ? String(fallback.message) : null,
        engineSelection: parsePageNumbers(fallback.engineSelection),
        userSelection:
          fallback.userSelection === undefined || fallback.userSelection === null
            ? null
            : parsePageNumbers(fallback.userSelection),
        resolution:
          fallback.resolution === 'print' || fallback.resolution === 'allA4' || fallback.resolution === 'unanswered'
            ? fallback.resolution
            : null,
        decisionMs:
          fallback.decisionMs === undefined || fallback.decisionMs === null
            ? null
            : Number(fallback.decisionMs),
        trace: isJsonObject(fallback.trace) ? fallback.trace : null,
        thumbnailsRef: fallback.thumbnailsRef ? String(fallback.thumbnailsRef) : null
      });
    }

    return res.status(201).json({ job });
  });

  router.post('/agents/me/jobs/:jobId/events', requireAgent, async (req, res) => {
    const level = req.body?.level;
    if (level !== 'info' && level !== 'warning' && level !== 'error') {
      return res.status(400).json({ error: 'INVALID_LEVEL' });
    }

    await store.recordEvent({
      agentJobId: req.params.jobId,
      level,
      code: String(req.body?.code ?? 'event'),
      detail: isJsonObject(req.body?.detail) ? req.body.detail : null
    });

    return res.status(202).json({ ok: true });
  });

  // -------------------------------------------------------------------------------------------
  // Admin-facing
  // -------------------------------------------------------------------------------------------

  const admin = [requireAuth, requireRole(ROLES.ADMIN)];

  router.post('/admin/agents/enrollment-tokens', ...admin, async (req, res) => {
    const hours = Number(req.body?.validForHours ?? 24);
    if (!Number.isFinite(hours) || hours <= 0 || hours > 24 * 30) {
      return res.status(400).json({ error: 'INVALID_VALIDITY' });
    }

    const token = await store.createEnrollmentToken({
      label: req.body?.label ? String(req.body.label) : null,
      expiresAt: new Date(Date.now() + hours * 60 * 60 * 1000),
      maxUses: Number(req.body?.maxUses ?? 1),
      createdBy: req.user?.id ?? null
    });

    return res.status(201).json(token);
  });

  router.get('/admin/agents', ...admin, async (_req, res) => {
    return res.json({ agents: await store.listAgents() });
  });

  router.get('/admin/agents/:agentId', ...admin, async (req, res) => {
    const agent = await store.getAgent(req.params.agentId);
    if (!agent) {
      return res.status(404).json({ error: 'AGENT_NOT_FOUND' });
    }

    return res.json({
      agent,
      printers: await store.listPrinters(agent.id),
      jobs: await store.listJobs({ agentId: agent.id, limit: 50 })
    });
  });

  router.get('/admin/rule-sets', ...admin, async (_req, res) => {
    return res.json({ ruleSets: await store.listRuleSets() });
  });

  router.post('/admin/rule-sets', ...admin, async (req, res) => {
    const name = String(req.body?.name ?? '').trim();
    if (!name || !isJsonObject(req.body?.rules)) {
      return res.status(400).json({ error: 'INVALID_RULE_SET' });
    }

    const ruleSet = await store.saveRuleSet({
      name,
      rules: req.body.rules,
      createdBy: req.user?.id ?? null
    });

    return res.status(201).json({ ruleSet });
  });

  router.post('/admin/bundles', ...admin, async (req, res) => {
    if (!isJsonObject(req.body?.payload)) {
      return res.status(400).json({ error: 'INVALID_BUNDLE' });
    }

    try {
      // Publishing is the last point at which a bad rule set costs one HTTP response instead
      // of every workstation in the building.
      parseBundlePayload(req.body.payload);
    } catch (error) {
      if (error instanceof WireFormatError) {
        return res.status(400).json({ error: 'INVALID_BUNDLE', detail: error.message });
      }
      throw error;
    }

    const bundle = await store.publishBundle({
      payload: req.body.payload,
      notes: req.body?.notes ? String(req.body.notes) : null,
      publishedBy: req.user?.id ?? null
    });

    return res.status(201).json({ bundle });
  });

  router.get('/admin/bundles/latest', ...admin, async (_req, res) => {
    const bundle = await store.latestBundle();
    return bundle ? res.json({ bundle }) : res.status(404).json({ error: 'NO_BUNDLE_PUBLISHED' });
  });

  router.get('/admin/agent-jobs', ...admin, async (req, res) => {
    return res.json({
      jobs: await store.listJobs({
        agentId: req.query.agentId ? String(req.query.agentId) : undefined,
        limit: req.query.limit ? Number(req.query.limit) : undefined
      })
    });
  });

  router.get('/admin/fallbacks', ...admin, async (req, res) => {
    return res.json({
      fallbacks: await store.listFallbacks({
        reasonCode: req.query.reasonCode ? String(req.query.reasonCode) : undefined,
        limit: req.query.limit ? Number(req.query.limit) : undefined
      })
    });
  });

  router.get('/admin/fallbacks/summary', ...admin, async (_req, res) => {
    return res.json({ summary: await store.summariseFallbacks() });
  });

  router.get('/admin/review-queue', ...admin, async (req, res) => {
    const status = req.query.status;
    const filter =
      status === 'OPEN' || status === 'RESOLVED' || status === 'DISMISSED' ? status : undefined;
    return res.json({ items: await store.listReviewQueue(filter) });
  });

  router.post('/admin/review-queue/:id/resolve', ...admin, async (req, res) => {
    const status = req.body?.status;
    if (status !== 'RESOLVED' && status !== 'DISMISSED') {
      return res.status(400).json({ error: 'INVALID_STATUS' });
    }

    const item = await store.resolveReviewItem({
      id: req.params.id,
      status,
      resolution: req.body?.resolution ? String(req.body.resolution) : null,
      proposedRule: isJsonObject(req.body?.proposedRule) ? req.body.proposedRule : null,
      resolvedBy: req.user?.id ?? null
    });

    return item ? res.json({ item }) : res.status(404).json({ error: 'REVIEW_ITEM_NOT_FOUND' });
  });

  router.get('/admin/retention', ...admin, async (_req, res) => {
    return res.json({ policies: await store.listRetentionPolicies() });
  });

  router.put('/admin/retention/:scope', ...admin, async (req, res) => {
    const retainDays = Number(req.body?.retainDays);
    if (!Number.isInteger(retainDays) || retainDays < 1 || retainDays > 3650) {
      return res.status(400).json({ error: 'INVALID_RETENTION' });
    }

    const policy = await store.setRetentionPolicy(req.params.scope, retainDays);
    return policy ? res.json({ policy }) : res.status(404).json({ error: 'SCOPE_NOT_FOUND' });
  });

  router.post('/admin/retention/run', ...admin, async (_req, res) => {
    return res.json({ removed: await store.applyRetention() });
  });

  return router;
}
