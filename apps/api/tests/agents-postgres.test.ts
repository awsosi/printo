import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import request from 'supertest';
import { Pool } from 'pg';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { BUILTIN_PROFILES, type ConformanceSuite } from '@printo/routing-engine';
import { createApiApp } from '../src/app.js';
import { PostgresAuthStore } from '../src/store/postgres-auth-store.js';
import { PostgresAgentStore } from '../src/agents/store.js';
import { PostgresAdvisoryLock, RetentionScheduler } from '../src/agents/retention.js';

/**
 * The agent API, against a real database.
 *
 * Deliberately not an in-memory double: almost everything worth testing here *is* the SQL —
 * the single-use token claim, the upsert that keeps a re-enrolled machine one agent, the
 * wholesale printer replacement, the fallback aggregation. A hand-written double would
 * reproduce the intent and hide a mistake in the statement.
 *
 * Skipped unless PRINTO_TEST_DATABASE_URL points at a migrated database, so the default unit
 * run needs no services:
 *
 *   docker compose -f infra/docker-compose.yml up -d db
 *   DATABASE_URL=postgres://printo:printo@127.0.0.1:5432/printo npm run migrate -w @printo/api
 *   PRINTO_TEST_DATABASE_URL=postgres://printo:printo@127.0.0.1:5432/printo npm run test -w @printo/api
 */
const connectionString = process.env.PRINTO_TEST_DATABASE_URL;
const suite = connectionString ? describe : describe.skip;

/**
 * Documents for the decision tests, taken from the engine's own conformance fixtures.
 *
 * Reusing them rather than inventing new page features is the point: if the server's
 * `/decide` ever answers differently from the engine both sides run, one of these tests and
 * the conformance suite disagree, which is exactly the divergence that must not go unnoticed.
 */
const FIXTURES: ConformanceSuite = JSON.parse(
  readFileSync(
    resolve(dirname(fileURLToPath(import.meta.url)), '../../../tests/conformance/lazy-ocr-and-fallbacks.json'),
    'utf8'
  )
);

function fixtureDocument(name: string): unknown {
  const fixture = FIXTURES.fixtures.find((entry) => entry.name === name);
  if (!fixture) {
    throw new Error(`conformance fixture '${name}' is gone; the decision tests reference it`);
  }
  return fixture.document;
}

suite('agent API (postgres)', () => {
  let pool: Pool;
  let app: ReturnType<typeof createApiApp>;
  let adminToken: string;

  beforeAll(async () => {
    pool = new Pool({ connectionString });

    // Both stores on Postgres: `created_by` on tokens and rule sets is a real foreign key to
    // `users`, so an in-memory auth store would hand out ids the database has never seen.
    app = createApiApp(new PostgresAuthStore(pool), new PostgresAgentStore(pool));

    await pool.query(`DELETE FROM users WHERE username = 'agent-admin'`);
    await request(app)
      .post('/auth/register')
      .send({ username: 'agent-admin', password: 'AdminPass123!', roles: ['ADMIN'] });

    const login = await request(app)
      .post('/auth/login')
      .send({ username: 'agent-admin', password: 'AdminPass123!' });

    adminToken = login.body.accessToken;
    expect(adminToken).toBeTruthy();
  });

  afterAll(async () => {
    await pool.query(`DELETE FROM users WHERE username = 'agent-admin'`);
    await pool.end();
  });

  beforeEach(async () => {
    // Cascades clear the pages, traces, events and fallbacks with their jobs and agents.
    await pool.query('DELETE FROM agent_jobs');
    await pool.query('DELETE FROM agents');
    await pool.query('DELETE FROM agent_enrollment_tokens');
    await pool.query('DELETE FROM rule_bundles');
    await pool.query('DELETE FROM review_queue');
    await pool.query('DELETE FROM fallback_events');
  });

  async function newToken(overrides: Record<string, unknown> = {}) {
    const response = await request(app)
      .post('/admin/agents/enrollment-tokens')
      .set('authorization', `Bearer ${adminToken}`)
      .send({ label: 'test', validForHours: 1, ...overrides });

    expect(response.status).toBe(201);
    return response.body.token as string;
  }

  /** A publishable bundle carrying the profiles the agent also ships with. */
  function bundlePayload(): Record<string, unknown> {
    return {
      schemaVersion: 1,
      profiles: JSON.parse(JSON.stringify(BUILTIN_PROFILES)),
      generatedAt: new Date().toISOString()
    };
  }

  async function enroll(token: string, installId = 'install-1') {
    return request(app).post('/agents/enroll').send({
      token,
      machineName: 'WS-001',
      installId,
      osVersion: 'Windows 11 26200',
      agentVersion: '0.1.0'
    });
  }

  it('enrols a machine and issues a key exactly once', async () => {
    const token = await newToken();
    const response = await enroll(token);

    expect(response.status).toBe(201);
    expect(response.body.apiKey).toBeTruthy();
    expect(response.body.agent.machineName).toBe('WS-001');

    // The key is never retrievable again; only its hash is stored.
    const stored = await pool.query('SELECT api_key_hash FROM agents');
    expect(stored.rows[0].api_key_hash).not.toBe(response.body.apiKey);
  });

  it('refuses to reuse a single-use enrolment token', async () => {
    const token = await newToken();

    expect((await enroll(token, 'install-a')).status).toBe(201);

    // A token that stays valid after use is a standing invitation to enrol an unmanaged
    // machine into the print fleet.
    const second = await enroll(token, 'install-b');
    expect(second.status).toBe(403);
    expect((await pool.query('SELECT COUNT(*)::int AS n FROM agents')).rows[0].n).toBe(1);
  });

  it('honours a multi-use token up to its limit', async () => {
    const token = await newToken({ maxUses: 2 });

    expect((await enroll(token, 'install-a')).status).toBe(201);
    expect((await enroll(token, 'install-b')).status).toBe(201);
    expect((await enroll(token, 'install-c')).status).toBe(403);
  });

  it('rejects an expired token', async () => {
    const token = await newToken();
    await pool.query(`UPDATE agent_enrollment_tokens SET expires_at = NOW() - INTERVAL '1 hour'`);

    expect((await enroll(token)).status).toBe(403);
  });

  it('treats a re-enrolled install as the same agent with a new key', async () => {
    const first = await enroll(await newToken());
    const second = await enroll(await newToken());

    expect(second.status).toBe(201);
    expect(second.body.agent.id).toBe(first.body.agent.id);
    expect(second.body.apiKey).not.toBe(first.body.apiKey);

    // The old key stops working the moment the machine re-enrols.
    const stale = await request(app).get('/agents/me').set('x-printo-agent-key', first.body.apiKey);
    expect(stale.status).toBe(401);
  });

  it('refuses agent endpoints without a valid key', async () => {
    expect((await request(app).get('/agents/me')).status).toBe(401);
    expect((await request(app).get('/agents/me').set('x-printo-agent-key', 'nope')).status).toBe(401);
  });

  it('serves the bundle and answers 304 when the agent is current', async () => {
    const enrolled = await enroll(await newToken());
    const key = enrolled.body.apiKey;

    expect((await request(app).get('/agents/me/bundle').set('x-printo-agent-key', key)).status).toBe(404);

    const published = await request(app)
      .post('/admin/bundles')
      .set('authorization', `Bearer ${adminToken}`)
      .send({ payload: bundlePayload(), notes: 'first' });

    expect(published.status).toBe(201);
    const version = published.body.bundle.version;

    const fetched = await request(app).get('/agents/me/bundle').set('x-printo-agent-key', key);
    expect(fetched.status).toBe(200);
    expect(fetched.body.version).toBe(version);
    expect(fetched.body.checksum).toHaveLength(64);

    // A fleet of 30 agents must not re-download an unchanged bundle on every poll.
    const unchanged = await request(app)
      .get(`/agents/me/bundle?since=${version}`)
      .set('x-printo-agent-key', key);
    expect(unchanged.status).toBe(304);
  });

  it('refuses to publish a bundle either engine could not execute', async () => {
    const broken = bundlePayload() as { profiles: Array<Record<string, unknown>> };
    (broken.profiles[0].pageRules as Array<Record<string, unknown>>)[0].when = {
      geometry: { inkAspect: { min: 2, max: 1 } }
    };

    const rejected = await request(app)
      .post('/admin/bundles')
      .set('authorization', `Bearer ${adminToken}`)
      .send({ payload: broken });

    expect(rejected.status).toBe(400);
    expect(rejected.body.error).toBe('INVALID_BUNDLE');
    // The path matters more than the message: an admin has to be able to find the rule.
    expect(rejected.body.detail).toContain('bundle.profiles[0].pageRules[0].when.geometry.inkAspect');

    const unknown = bundlePayload() as { profiles: Array<Record<string, unknown>> };
    (unknown.profiles[0].pageRules as Array<Record<string, unknown>>)[0].when = { colour: { is: 'red' } };

    const alsoRejected = await request(app)
      .post('/admin/bundles')
      .set('authorization', `Bearer ${adminToken}`)
      .send({ payload: unknown });

    expect(alsoRejected.status).toBe(400);
    expect(alsoRejected.body.detail).toContain('unknown predicate');

    // Nothing reached the fleet.
    expect((await pool.query('SELECT COUNT(*)::int AS n FROM rule_bundles')).rows[0].n).toBe(0);
  });

  it('decides a document server-side from features alone', async () => {
    const key = (await enroll(await newToken())).body.apiKey;

    const decided = await request(app)
      .post('/agents/me/decide')
      .set('x-printo-agent-key', key)
      .send({ features: fixtureDocument('FedEx label geometry decides without any OCR') });

    expect(decided.status).toBe(200);
    expect(decided.body.status).toBe('decided');
    expect(decided.body.decision.pages[0].route).toBe('THERMAL');
    // No bundle published: the server decided on the same built-in profiles the agent ships.
    expect(decided.body.bundleVersion).toBeNull();
  });

  it('carries the two-phase OCR protocol across the network', async () => {
    const key = (await enroll(await newToken())).body.apiKey;
    const features = fixtureDocument(
      'A DHL-shaped page with no usable text stops and asks for OCR of the ink box'
    );

    const first = await request(app)
      .post('/agents/me/decide')
      .set('x-printo-agent-key', key)
      .send({ features });

    expect(first.status).toBe(200);
    expect(first.body.status).toBe('needs-features');
    expect(first.body.ocr).toHaveLength(1);
    expect(first.body.ocr[0].pageNumber).toBe(1);

    // The agent is the only side holding the pixels, so it fills the region and asks again.
    const enriched = fixtureDocument(
      'OCR recovers the waybill markings the anonymiser flattened into an image'
    );

    const second = await request(app)
      .post('/agents/me/decide')
      .set('x-printo-agent-key', key)
      .send({ features: enriched, secondPass: true });

    expect(second.status).toBe(200);
    expect(second.body.status).toBe('decided');
    expect(second.body.decision.pages[0].route).toBe('A4');

    // A second pass that still asks is a rule-set defect, not a question for the user.
    const looping = await request(app)
      .post('/agents/me/decide')
      .set('x-printo-agent-key', key)
      .send({ features, secondPass: true });

    expect(looping.status).toBe(422);
    expect(looping.body.error).toBe('RULES_ASK_OCR_TWICE');
  });

  it('rejects malformed features rather than throwing', async () => {
    const key = (await enroll(await newToken())).body.apiKey;

    const response = await request(app)
      .post('/agents/me/decide')
      .set('x-printo-agent-key', key)
      .send({ features: { fileName: 'x.pdf', pages: [{ pageNumber: 1 }] } });

    expect(response.status).toBe(400);
    expect(response.body.error).toBe('INVALID_FEATURES');
    expect(response.body.detail).toContain('features.pages[0]');

    expect((await request(app).post('/agents/me/decide').send({})).status).toBe(401);
  });

  it('decides with the published bundle once one exists', async () => {
    const key = (await enroll(await newToken())).body.apiKey;

    // A bundle whose only rule sends every page to thermal: proves the server executes what
    // was published rather than its own built-ins.
    const published = await request(app)
      .post('/admin/bundles')
      .set('authorization', `Bearer ${adminToken}`)
      .send({
        payload: {
          schemaVersion: 1,
          profiles: [
            {
              profile: 'EverythingThermal',
              version: 1,
              pageRules: [
                {
                  id: 'all-thermal',
                  name: 'Everything is a label',
                  when: { pageIndex: { range: { min: 1 } } },
                  then: { route: 'THERMAL', confidence: 1 }
                }
              ],
              fallback: { route: 'A4', onUnknown: 'prompt' }
            }
          ]
        }
      });

    expect(published.status).toBe(201);

    const decided = await request(app)
      .post('/agents/me/decide')
      .set('x-printo-agent-key', key)
      .send({ features: fixtureDocument('An A4 invoice takes the profile default silently') });

    expect(decided.status).toBe(200);
    expect(decided.body.decision.profile).toBe('EverythingThermal');
    expect(decided.body.decision.pages[0].route).toBe('THERMAL');
    expect(decided.body.bundleVersion).toBe(published.body.bundle.version);
  });

  it('replaces the reported printers wholesale', async () => {
    const key = (await enroll(await newToken())).body.apiKey;

    await request(app)
      .post('/agents/me/printers')
      .set('x-printo-agent-key', key)
      .send({
        printers: [
          { queueName: 'HP LaserJet', role: 'A4' },
          { queueName: 'Zebra', role: 'THERMAL', media: '100x150mm', darkness: 12 }
        ]
      });

    const second = await request(app)
      .post('/agents/me/printers')
      .set('x-printo-agent-key', key)
      .send({ printers: [{ queueName: 'HP LaserJet', role: 'A4' }] });

    // A printer removed from the workstation has to disappear here too, or the admin UI
    // offers a queue that no longer exists.
    expect(second.status).toBe(200);
    expect(second.body.printers).toHaveLength(1);
    expect(second.body.printers[0].queueName).toBe('HP LaserJet');
  });

  it('records a job with its pages, traces and fallback, and keeps it idempotent', async () => {
    const key = (await enroll(await newToken())).body.apiKey;

    const body = {
      jobKey: 'folder:abc123',
      source: 'HotFolder',
      fileName: 'OneClickPrint_TEST.pdf',
      documentSha256: 'abc123',
      pageCount: 2,
      status: 'COMPLETED',
      pages: [
        {
          pageNumber: 1,
          route: 'A4',
          ruleId: null,
          confidence: 0.6,
          traces: [
            {
              ruleId: 'dhl-label-embedded',
              outcome: 'failed',
              failedPredicate: 'geometry inkAspect 1.75..2.2',
              measured: { inkAspect: 1.23 }
            }
          ]
        },
        {
          pageNumber: 2,
          route: 'THERMAL',
          ruleId: 'fedex-label-embedded',
          carrier: 'FEDEX',
          confidence: 0.95,
          printerQueue: 'Zebra',
          transform: { source: 'inkBox', media: '100x150mm', mediaFrom: 'agent-printer' }
        }
      ],
      fallback: {
        reasonCode: 'LOW_CONFIDENCE',
        message: 'below threshold',
        engineSelection: [2],
        userSelection: [2],
        resolution: 'print',
        decisionMs: 1400
      }
    };

    const created = await request(app)
      .post('/agents/me/jobs')
      .set('x-printo-agent-key', key)
      .send(body);
    expect(created.status).toBe(201);

    // A re-report of the same key updates rather than duplicating, mirroring the agent spool.
    const repeated = await request(app)
      .post('/agents/me/jobs')
      .set('x-printo-agent-key', key)
      .send({ ...body, status: 'COMPLETED' });
    expect(repeated.status).toBe(201);
    expect(repeated.body.job.id).toBe(created.body.job.id);

    expect((await pool.query('SELECT COUNT(*)::int AS n FROM agent_jobs')).rows[0].n).toBe(1);
    expect((await pool.query('SELECT COUNT(*)::int AS n FROM agent_job_pages')).rows[0].n).toBe(2);

    // The trace names the failing predicate and the value it measured — the thing that turns
    // "routing failed" into a rule an admin can fix.
    const trace = await pool.query(
      'SELECT failed_predicate, measured FROM agent_job_page_traces LIMIT 1'
    );
    expect(trace.rows[0].failed_predicate).toContain('inkAspect');
    expect(trace.rows[0].measured).toEqual({ inkAspect: 1.23 });
  });

  it('summarises fallbacks by reason, including agreement with the engine', async () => {
    const key = (await enroll(await newToken())).body.apiKey;

    async function report(jobKey: string, fallback: Record<string, unknown>) {
      await request(app)
        .post('/agents/me/jobs')
        .set('x-printo-agent-key', key)
        .send({
          jobKey,
          source: 'HotFolder',
          fileName: `${jobKey}.pdf`,
          documentSha256: jobKey,
          status: 'COMPLETED',
          fallback
        });
    }

    await report('j1', {
      reasonCode: 'LOW_CONFIDENCE',
      engineSelection: [2],
      userSelection: [2],
      resolution: 'print',
      decisionMs: 1000
    });
    await report('j2', {
      reasonCode: 'LOW_CONFIDENCE',
      engineSelection: [2],
      userSelection: [3],
      resolution: 'print',
      decisionMs: 3000
    });
    await report('j3', {
      reasonCode: 'OCR_UNAVAILABLE',
      engineSelection: [],
      userSelection: null,
      resolution: 'unanswered'
    });

    const summary = await request(app)
      .get('/admin/fallbacks/summary')
      .set('authorization', `Bearer ${adminToken}`);

    expect(summary.status).toBe(200);
    const low = summary.body.summary.find(
      (row: { reasonCode: string }) => row.reasonCode === 'LOW_CONFIDENCE'
    );

    // Two fallbacks for the same reason, both answered, one where the user simply confirmed
    // the engine. A high agreement rate means the threshold is too tight; a low one means the
    // rule is actually wrong, and telling them apart is the point of the view.
    expect(low.total).toBe(2);
    expect(low.answered).toBe(2);
    expect(low.agreedWithEngine).toBe(1);
    expect(low.medianDecisionMs).toBe(2000);

    // Answered fallbacks become review items, which is how one becomes a rule in one click.
    const queue = await request(app)
      .get('/admin/review-queue?status=OPEN')
      .set('authorization', `Bearer ${adminToken}`);
    expect(queue.body.items).toHaveLength(2);
  });

  it('resolves a review item with the rule it proposes', async () => {
    const key = (await enroll(await newToken())).body.apiKey;
    await request(app)
      .post('/agents/me/jobs')
      .set('x-printo-agent-key', key)
      .send({
        jobKey: 'r1',
        source: 'HotFolder',
        fileName: 'r1.pdf',
        documentSha256: 'r1',
        status: 'COMPLETED',
        fallback: {
          reasonCode: 'UNKNOWN_CARRIER',
          engineSelection: [],
          userSelection: [1],
          resolution: 'print'
        }
      });

    const queue = await request(app)
      .get('/admin/review-queue')
      .set('authorization', `Bearer ${adminToken}`);
    const item = queue.body.items[0];

    const resolved = await request(app)
      .post(`/admin/review-queue/${item.id}/resolve`)
      .set('authorization', `Bearer ${adminToken}`)
      .send({
        status: 'RESOLVED',
        resolution: 'added a DPD template',
        proposedRule: { id: 'dpd-label', when: { carrier: { is: 'DPD' } }, then: { route: 'THERMAL' } }
      });

    expect(resolved.status).toBe(200);
    expect(resolved.body.item.status).toBe('RESOLVED');
    expect(resolved.body.item.proposedRule.id).toBe('dpd-label');
  });

  it('keeps admin endpoints away from agents and agent endpoints away from admins', async () => {
    const key = (await enroll(await newToken())).body.apiKey;

    // An agent key is not an admin credential, however valid it is.
    expect((await request(app).get('/admin/agents').set('x-printo-agent-key', key)).status).toBe(401);

    // And an admin JWT is not an agent key.
    expect(
      (await request(app).get('/agents/me').set('authorization', `Bearer ${adminToken}`)).status
    ).toBe(401);
  });

  it('applies retention windows to the tables it owns', async () => {
    const key = (await enroll(await newToken())).body.apiKey;
    await request(app)
      .post('/agents/me/jobs')
      .set('x-printo-agent-key', key)
      .send({
        jobKey: 'old',
        source: 'HotFolder',
        fileName: 'old.pdf',
        documentSha256: 'old',
        status: 'COMPLETED'
      });

    await pool.query(`UPDATE agent_jobs SET created_at = NOW() - INTERVAL '400 days'`);

    const run = await request(app)
      .post('/admin/retention/run')
      .set('authorization', `Bearer ${adminToken}`);

    expect(run.status).toBe(200);
    expect(run.body.removed.job_history).toBe(1);
    expect((await pool.query('SELECT COUNT(*)::int AS n FROM agent_jobs')).rows[0].n).toBe(0);
  });

  it('sweeps on a schedule and only once across replicas', async () => {
    const key = (await enroll(await newToken())).body.apiKey;
    await request(app)
      .post('/agents/me/jobs')
      .set('x-printo-agent-key', key)
      .send({
        jobKey: 'stale',
        source: 'HotFolder',
        fileName: 'stale.pdf',
        documentSha256: 'stale',
        status: 'COMPLETED'
      });

    await pool.query(`UPDATE agent_jobs SET created_at = NOW() - INTERVAL '400 days'`);

    const store = new PostgresAgentStore(pool);
    const events: Record<string, unknown>[] = [];
    const scheduler = new RetentionScheduler(store, {
      lock: new PostgresAdvisoryLock(pool),
      log: (event) => events.push(event)
    });

    const removed = await scheduler.runOnce();
    expect(removed?.job_history).toBe(1);
    expect(events.at(-1)?.event).toBe('retention_applied');

    // A second replica holding the lock must skip rather than sweep in parallel: the counts
    // and `last_run_at` have to describe one run, not an interleaving of two.
    const holder = await pool.connect();
    try {
      await holder.query('SELECT pg_advisory_lock($1)', [PostgresAdvisoryLock.RETENTION_KEY]);

      const blocked = new RetentionScheduler(store, {
        lock: new PostgresAdvisoryLock(pool),
        log: (event) => events.push(event)
      });

      expect(await blocked.runOnce()).toBeNull();
      expect(events.at(-1)).toMatchObject({ event: 'retention_skipped', reason: 'locked' });
    } finally {
      await holder.query('SELECT pg_advisory_unlock($1)', [PostgresAdvisoryLock.RETENTION_KEY]);
      holder.release();
    }

    // And the lock is released after a sweep, so the next one is not blocked by the last.
    expect(await scheduler.runOnce()).not.toBeNull();
  });

  it('logs a failed sweep instead of taking the API down with it', async () => {
    const events: Record<string, unknown>[] = [];
    const scheduler = new RetentionScheduler(
      { applyRetention: async () => { throw new Error('database is on fire'); } } as unknown as PostgresAgentStore,
      { log: (event) => events.push(event) }
    );

    expect(await scheduler.runOnce()).toBeNull();
    expect(events.at(-1)).toMatchObject({ event: 'retention_failed', error: 'database is on fire' });
  });
});
