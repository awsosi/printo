import request from 'supertest';
import { describe, expect, it, vi } from 'vitest';
import { createWebApp } from '../src/app.js';

/**
 * The fleet tab of the admin console.
 *
 * The page is server-rendered as one string, so what can be asserted here is that the markup
 * and the script that drives it are present and wired to the right endpoints, and that every
 * fleet call the browser makes is proxied with the operator's token rather than reaching the
 * API from the browser with a second credential.
 */
describe('fleet admin', () => {
  it('renders the fleet tab with all four surfaces', async () => {
    const res = await request(createWebApp()).get('/admin/config');

    expect(res.status).toBe(200);
    expect(res.text).toContain('data-tab="fleet">Fleet</button>');
    expect(res.text).toContain('id="panel-fleet"');

    // Agents, rules, fallbacks, review queue - the four questions the tab exists to answer.
    expect(res.text).toContain('id="fleetAgentList"');
    expect(res.text).toContain('id="fleetBundleEditor"');
    expect(res.text).toContain('id="fleetFallbackSummary"');
    expect(res.text).toContain('id="fleetReviewList"');

    // And it is actually driven, not decorative.
    expect(res.text).toContain('function loadFleet()');
    expect(res.text).toContain('bindFleet();');
  });

  it('proxies every fleet endpoint with the operator token', async () => {
    const seen: Array<{ url: string; method: string; auth: string | undefined }> = [];
    const fetchMock = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
      seen.push({
        url: String(url),
        method: init?.method ?? 'GET',
        auth: (init?.headers as Record<string, string> | undefined)?.authorization
      });
      return new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { 'content-type': 'application/json' }
      });
    });

    const app = createWebApp({ fetchImpl: fetchMock as unknown as typeof fetch, apiBaseUrl: 'http://api:4000' });

    const calls: Array<[string, string, unknown?]> = [
      ['get', '/admin/agents'],
      ['get', '/admin/bundles/latest'],
      ['get', '/admin/fallbacks/summary'],
      ['get', '/admin/review-queue?status=OPEN'],
      ['post', '/admin/bundles', { payload: { schemaVersion: 1, profiles: [] } }],
      ['patch', '/admin/agents/agent-1', { status: 'DISABLED' }],
      ['post', '/admin/agents/enrollment-tokens', { validForHours: 1 }],
      ['post', '/admin/review-queue/item-1/resolve', { status: 'RESOLVED' }]
    ];

    for (const [method, path, body] of calls) {
      const pending = (request(app) as unknown as Record<string, (p: string) => request.Test>)[method](path)
        .set('authorization', 'Bearer operator-token');
      const response = await (body === undefined ? pending : pending.send(body as object));
      expect(response.status, `${method} ${path}`).toBe(200);
    }

    expect(seen).toHaveLength(calls.length);
    expect(seen.every((call) => call.auth === 'Bearer operator-token')).toBe(true);

    // Query strings survive the hop: the review queue is filtered server-side, not in the page.
    expect(seen[3].url).toBe('http://api:4000/admin/review-queue?status=OPEN');
    expect(seen[5]).toMatchObject({ url: 'http://api:4000/admin/agents/agent-1', method: 'PATCH' });
  });

  it('refuses fleet calls that arrive without a token', async () => {
    const fetchMock = vi.fn();
    const app = createWebApp({ fetchImpl: fetchMock as unknown as typeof fetch });

    const response = await request(app).get('/admin/agents');

    expect(response.status).toBe(401);
    expect(response.body).toEqual({ error: 'MISSING_AUTH_TOKEN' });

    // And nothing reached the API: an unauthenticated browser must not be able to make the
    // console fan out requests on its behalf.
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('passes the API rejection through so a bad bundle names its own path', async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response(
          JSON.stringify({
            error: 'INVALID_BUNDLE',
            detail: 'bundle.profiles[0].pageRules[0].when.colour: unknown predicate'
          }),
          { status: 400, headers: { 'content-type': 'application/json' } }
        )
    );

    const app = createWebApp({ fetchImpl: fetchMock as unknown as typeof fetch });
    const response = await request(app)
      .post('/admin/bundles')
      .set('authorization', 'Bearer operator-token')
      .send({ payload: {} });

    // The path is the whole value of the validation: swallowing it and showing "rejected"
    // would leave an administrator hunting through a rule set by hand.
    expect(response.status).toBe(400);
    expect(response.body.detail).toContain('pageRules[0].when.colour');
  });

  it('shows the API detail in the page, not just the error code', async () => {
    const res = await request(createWebApp()).get('/admin/config');

    // Found by driving the real console: the proxy passed `detail` through faithfully and the
    // page threw it away, so a rejected bundle read "INVALID_BUNDLE" and nothing else.
    expect(res.text).toContain("payload && payload.detail ? code + ': ' + payload.detail : code");
  });
});
