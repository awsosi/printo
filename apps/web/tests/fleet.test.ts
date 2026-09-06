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
    expect(res.text).toContain('id="fleetAccountingList"');
    expect(res.text).toContain('id="fleetReconciliation"');
    expect(res.text).toContain('id="fleetJobList"');
    expect(res.text).toContain('id="fleetJobDetail"');

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
      ['get', '/admin/accounting'],
      ['get', '/admin/agent-jobs?limit=12'],
      ['get', '/admin/agent-jobs/job-1'],
      ['post', '/admin/bundles', { payload: { schemaVersion: 1, profiles: [] } }],
      ['patch', '/admin/agents/agent-1', { status: 'DISABLED' }],
      ['post', '/admin/agents/enrollment-tokens', { validForHours: 1 }],
      ['post', '/admin/review-queue/item-1/resolve', { status: 'RESOLVED' }],
      ['post', '/admin/review-queue/item-1/propose-rule', {}]
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
    expect(seen.find((call) => call.url.endsWith('/admin/agents/agent-1'))).toMatchObject({ method: 'PATCH' });
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

  it('offers a proposed rule, and adopts it into the editor rather than publishing it', async () => {
    const res = await request(createWebApp()).get('/admin/config');

    expect(res.text).toContain('data-review-propose=');
    expect(res.text).toContain('function renderProposal(proposal)');
    expect(res.text).toContain('data-review-adopt=');

    // Adopting writes into the editor. Publishing a machine-written rule to the whole fleet on
    // one click is exactly what this must not do.
    expect(res.text).toContain("fleetSetStatus('fleetBundleStatus', 'Added to the editor. Review it, then publish.', 'ok')");

    // And it goes in front of the rules it was derived to fix, or it would never match.
    expect(res.text).toContain('profile.pageRules = [rule].concat(profile.pageRules || [])');
  });

  it('offers a template cropper that cuts in the browser and never uploads the sample', async () => {
    const res = await request(createWebApp()).get('/admin/config');

    expect(res.text).toContain('id="fleetSampleCanvas"');
    expect(res.text).toContain('id="fleetAddTemplateButton"');
    expect(res.text).toContain('function fleetCutTemplate()');

    // The sample never leaves the browser: there is no upload endpoint and none is called.
    expect(res.text).toContain('the document is never uploaded');

    // Canvas pixels, not CSS pixels. The canvas is displayed scaled to fit, so cropping on
    // CSS coordinates would cut the wrong region on every screen but the one it was written on.
    expect(res.text).toContain('canvas.width / bounds.width');

    // Recorded with the template, because the agent looks for the logo at the physical size it
    // was cut at rather than at whatever pixel size the preview happened to use.
    expect(res.text).toContain('const FLEET_TEMPLATE_DPI = 150');
    expect(res.text).toContain('dpi: FLEET_TEMPLATE_DPI');
  });

  it('shows the media each page printed on and which layer chose it', async () => {
    const res = await request(createWebApp()).get('/admin/config');

    // The value alone is not an explanation: media comes through a five-layer precedence chain,
    // so the page has to name the layer that supplied it.
    expect(res.text).toContain('transform.effectiveMedia');
    expect(res.text).toContain("' (from ' + escapeHtml(transform.mediaSource || 'unknown') + ')'");
    expect(res.text).toContain('function renderFleetJobDetail()');

    // A reviewer should be able to see the page, not only read about it.
    expect(res.text).toContain("'/pages/' + page.pageNumber + '/thumbnail\"");
  });

  it('passes a thumbnail through as bytes rather than decoding it as text', async () => {
    // A PNG signature plus bytes that are not valid UTF-8. Decoding this as text replaces them
    // with U+FFFD, which produces a broken image and no error anywhere to explain it.
    const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0xff, 0xfe, 0x80, 0x01]);

    const fetchMock = vi.fn(
      async () =>
        new Response(png, {
          status: 200,
          headers: { 'content-type': 'image/png', 'cache-control': 'private, max-age=300' }
        })
    );

    const app = createWebApp({ fetchImpl: fetchMock as unknown as typeof fetch });
    const response = await request(app)
      .get('/admin/agent-jobs/job-1/pages/2/thumbnail')
      .set('authorization', 'Bearer operator-token')
      .responseType('blob');

    expect(response.status).toBe(200);
    expect(response.headers['content-type']).toBe('image/png');
    expect(Buffer.from(response.body).equals(png)).toBe(true);
  });

  it('shows the API detail in the page, not just the error code', async () => {
    const res = await request(createWebApp()).get('/admin/config');

    // Found by driving the real console: the proxy passed `detail` through faithfully and the
    // page threw it away, so a rejected bundle read "INVALID_BUNDLE" and nothing else.
    expect(res.text).toContain("payload && payload.detail ? code + ': ' + payload.detail : code");
  });
});
