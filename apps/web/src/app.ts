import express, { type Request, type Response } from 'express';
import { matchPdfPagesBySnippet } from '@printo/shared';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { getDefaultLocale, resolveMessages } from './i18n.js';

type FetchLike = typeof fetch;
type HttpMethod = 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE';
type ExpressMethod = 'get' | 'post' | 'patch' | 'put' | 'delete';

interface CreateWebAppOptions {
  fetchImpl?: FetchLike;
  apiBaseUrl?: string;
  workerBaseUrl?: string;
}

interface ProxyDefinition {
  method: ExpressMethod;
  path: string;
  upstreamPath: (req: Request) => string;
}

function resolveAuthHeader(req: Request): string | null {
  const directHeader = req.header('authorization');
  if (directHeader) {
    return directHeader;
  }

  const tokenHeader = req.header('x-auth-token');
  if (!tokenHeader) {
    return null;
  }

  return tokenHeader.startsWith('Bearer ') ? tokenHeader : `Bearer ${tokenHeader}`;
}

async function sendProxyRequest(input: {
  req: Request;
  res: Response;
  fetchImpl: FetchLike;
  apiBaseUrl: string;
  upstreamPath: string;
  method: HttpMethod;
}) {
  const { req, res, fetchImpl, apiBaseUrl, upstreamPath, method } = input;
  const authHeader = resolveAuthHeader(req);
  if (!authHeader) {
    return res.status(401).json({ error: 'MISSING_AUTH_TOKEN' });
  }

  const headers: Record<string, string> = {
    accept: 'application/json',
    authorization: authHeader
  };
  const requestInit: RequestInit = { method, headers };

  if (method !== 'GET' && method !== 'DELETE') {
    headers['content-type'] = 'application/json';
    requestInit.body = JSON.stringify(req.body ?? {});
  }

  try {
    const querySuffix = req.originalUrl.includes('?') ? req.originalUrl.slice(req.originalUrl.indexOf('?')) : '';
    const response = await fetchImpl(`${apiBaseUrl}${upstreamPath}${querySuffix}`, requestInit);
    const contentType = response.headers.get('content-type') ?? '';

    if (response.status === 204) {
      return res.status(204).send();
    }

    if (contentType.includes('application/json')) {
      return res.status(response.status).json(await response.json());
    }

    return res.status(response.status).send(await response.text());
  } catch {
    return res.status(502).json({ error: 'UPSTREAM_UNAVAILABLE' });
  }
}

function renderAdminPage(): string {
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Printo Admin</title>
    <style>
      :root {
        color-scheme: light dark;
        --bg: #f4efe4;
        --paper: rgba(255, 250, 242, 0.92);
        --paper-strong: #fffdfa;
        --line: #d8cebb;
        --text: #182018;
        --muted: #5e645f;
        --accent: #1d5a48;
        --accent-soft: #e1efe8;
        --danger: #b42318;
        --ok: #166534;
        --shadow: 0 18px 36px rgba(24, 32, 24, 0.08);
      }

      @media (prefers-color-scheme: dark) {
        :root {
          --bg: #0f1412;
          --paper: rgba(21, 28, 25, 0.94);
          --paper-strong: #17201c;
          --line: #304039;
          --text: #edf4ef;
          --muted: #9fb1a7;
          --accent: #5fb191;
          --accent-soft: rgba(95, 177, 145, 0.14);
          --danger: #ff8a80;
          --ok: #86efac;
          --shadow: 0 18px 36px rgba(0, 0, 0, 0.32);
        }
      }

      * {
        box-sizing: border-box;
      }

      body {
        margin: 0;
        color: var(--text);
        background:
          radial-gradient(circle at top left, rgba(29, 90, 72, 0.18), transparent 28%),
          linear-gradient(180deg, color-mix(in srgb, var(--bg) 96%, #ffffff 4%) 0%, var(--bg) 100%);
        font-family: "IBM Plex Sans", "Segoe UI", sans-serif;
      }

      button,
      input,
      select,
      textarea {
        width: 100%;
        font: inherit;
      }

      input,
      select,
      textarea,
      button {
        padding: 11px 12px;
        border-radius: 14px;
        border: 1px solid var(--line);
        background: var(--paper-strong);
        color: inherit;
      }

      textarea {
        min-height: 96px;
        resize: vertical;
      }

      button {
        cursor: pointer;
        font-weight: 600;
        border-color: transparent;
        background: var(--accent);
        color: #fff;
      }

      button.secondary {
        border-color: var(--line);
        background: transparent;
        color: var(--text);
      }

      button.ghost {
        border-style: dashed;
        border-color: var(--line);
        background: transparent;
        color: var(--text);
      }

      .shell {
        max-width: 1240px;
        margin: 0 auto;
        padding: 18px;
      }

      .hero,
      .auth-grid,
      .stack,
      .grid2,
      .grid3,
      .tab-panels,
      .preview-grid,
      .thumbnail-grid {
        display: grid;
        gap: 16px;
      }

      .hero {
        grid-template-columns: minmax(0, 1fr) 300px;
        margin-bottom: 16px;
      }

      .auth-grid {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }

      .grid2 {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }

      .grid3 {
        grid-template-columns: repeat(3, minmax(0, 1fr));
      }

      .preview-grid {
        grid-template-columns: minmax(260px, 320px) minmax(0, 1fr);
        align-items: start;
      }

      .thumbnail-grid {
        grid-template-columns: repeat(auto-fill, minmax(170px, 1fr));
      }

      .card {
        padding: 18px;
        border-radius: 22px;
        border: 1px solid var(--line);
        background: var(--paper);
        box-shadow: var(--shadow);
        backdrop-filter: blur(10px);
      }

      .section-title {
        display: flex;
        justify-content: space-between;
        gap: 12px;
        align-items: start;
      }

      .section-title h1,
      .section-title h2,
      .section-title h3,
      .card p {
        margin: 0;
      }

      .muted {
        color: var(--muted);
      }

      .status {
        min-height: 20px;
      }

      .ok {
        color: var(--ok);
      }

      .danger {
        color: var(--danger);
      }

      .hint {
        padding: 14px;
        border-radius: 16px;
        border: 1px solid color-mix(in srgb, var(--accent) 24%, var(--line));
        background: var(--accent-soft);
      }

      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
      }

      .actions button {
        width: auto;
      }

      .pill {
        display: inline-flex;
        align-items: center;
        width: auto;
        padding: 4px 10px;
        border-radius: 999px;
        border: 1px solid var(--line);
        background: color-mix(in srgb, var(--paper-strong) 88%, transparent);
        font-size: 0.84rem;
        white-space: nowrap;
      }

      .pill.warn {
        border-color: color-mix(in srgb, #d97706 40%, var(--line));
        background: color-mix(in srgb, #fff7ed 88%, transparent);
        color: #9a3412;
      }

      .pill.danger {
        border-color: color-mix(in srgb, var(--danger) 40%, var(--line));
        background: color-mix(in srgb, #fef2f2 88%, transparent);
      }

      .pill.ok {
        border-color: color-mix(in srgb, var(--ok) 40%, var(--line));
        background: color-mix(in srgb, #f0fdf4 88%, transparent);
      }

      .pill.info {
        border-color: color-mix(in srgb, var(--accent) 36%, var(--line));
        background: color-mix(in srgb, var(--accent-soft) 92%, transparent);
        color: var(--accent);
      }

      .tab-bar {
        display: flex;
        gap: 10px;
        overflow: auto;
        padding-bottom: 2px;
        margin-bottom: 16px;
      }

      .tab-bar button {
        width: auto;
        min-width: 140px;
        background: transparent;
        border: 1px solid var(--line);
        color: var(--text);
      }

      .tab-bar button.active {
        background: var(--accent);
        color: #fff;
      }

      .panel {
        display: none;
      }

      .panel.active {
        display: grid;
      }

      .list {
        display: grid;
        gap: 10px;
      }

      .stats-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
        gap: 10px;
      }

      .stat-card {
        padding: 14px;
        border-radius: 16px;
        border: 1px solid var(--line);
        background: color-mix(in srgb, var(--paper-strong) 94%, transparent);
      }

      .stat-card strong {
        display: block;
        font-size: 1.5rem;
        margin-top: 6px;
      }

      .status-layout {
        display: grid;
        gap: 16px;
      }

      .status-subgrid {
        display: grid;
        grid-template-columns: 1.1fr 1fr;
        gap: 16px;
      }

      .status-row {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
      }

      .status-row strong,
      .status-row span,
      .status-row p {
        margin: 0;
      }

      .status-row + .status-row {
        margin-top: 10px;
      }

      .status-meta {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }

      .status-table {
        display: grid;
        gap: 10px;
      }

      .status-table .item {
        padding: 12px 14px;
      }

      .status-label {
        display: inline-flex;
        align-items: center;
        gap: 8px;
      }

      .status-dot {
        width: 10px;
        height: 10px;
        border-radius: 999px;
        background: var(--muted);
        box-shadow: 0 0 0 3px color-mix(in srgb, currentColor 12%, transparent);
      }

      .status-dot.ok {
        background: var(--ok);
        color: var(--ok);
      }

      .status-dot.warn {
        background: #d97706;
        color: #d97706;
      }

      .status-dot.danger {
        background: var(--danger);
        color: var(--danger);
      }

      .status-dot.info {
        background: var(--accent);
        color: var(--accent);
      }

      .log-list {
        display: grid;
        gap: 8px;
      }

      .job-card {
        display: grid;
        gap: 10px;
      }

      .job-meta {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }

      .job-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }

      .job-actions button {
        width: auto;
        min-width: 124px;
      }

      .job-actions button[disabled] {
        cursor: not-allowed;
        opacity: 0.55;
      }

      .log-item {
        padding: 12px 14px;
        border-radius: 14px;
        border: 1px solid var(--line);
        background: color-mix(in srgb, var(--paper-strong) 94%, transparent);
      }

      .log-item header {
        display: flex;
        justify-content: space-between;
        gap: 12px;
        align-items: start;
      }

      .log-item p,
      .log-item h3 {
        margin: 0;
      }

      .log-item p + p {
        margin-top: 4px;
      }

      .item {
        padding: 14px;
        border-radius: 16px;
        border: 1px solid var(--line);
        background: color-mix(in srgb, var(--paper-strong) 92%, transparent);
      }

      .item header {
        display: flex;
        justify-content: space-between;
        align-items: start;
        gap: 12px;
      }

      .item h3,
      .item p {
        margin: 0;
      }

      .item p + p {
        margin-top: 6px;
      }

      .snippet-box {
        display: grid;
        gap: 12px;
      }

      .route-rule {
        display: grid;
        grid-template-columns: 1.3fr 0.9fr 1.1fr 88px 44px;
        gap: 8px;
        align-items: end;
        padding: 12px;
        border-radius: 16px;
        border: 1px solid var(--line);
        background: color-mix(in srgb, var(--paper-strong) 92%, transparent);
      }

      .route-rule label {
        display: grid;
        gap: 4px;
        font-size: 0.8rem;
        color: var(--muted);
      }

      .route-rule button {
        padding: 10px 0;
      }

      .thumbnail-tags {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
      }

      .thumbnail-tags .pill,
      .page-detail-row .pill {
        white-space: normal;
        overflow-wrap: anywhere;
      }

      .page-detail-table {
        display: grid;
        gap: 6px;
      }

      .page-detail-row {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        align-items: center;
        padding: 8px 10px;
        border-radius: 12px;
        border: 1px solid var(--line);
        background: color-mix(in srgb, var(--paper-strong) 90%, transparent);
        font-size: 0.86rem;
      }

      @media (max-width: 980px) {
        .route-rule {
          grid-template-columns: 1fr 1fr;
        }
      }

      .snippet-preview {
        width: 100%;
        min-height: 120px;
        max-height: 280px;
        object-fit: contain;
        border-radius: 18px;
        border: 1px dashed var(--line);
        background:
          linear-gradient(45deg, color-mix(in srgb, var(--paper-strong) 90%, transparent) 25%, transparent 25%),
          linear-gradient(-45deg, color-mix(in srgb, var(--paper-strong) 90%, transparent) 25%, transparent 25%),
          linear-gradient(45deg, transparent 75%, color-mix(in srgb, var(--paper-strong) 90%, transparent) 75%),
          linear-gradient(-45deg, transparent 75%, color-mix(in srgb, var(--paper-strong) 90%, transparent) 75%);
        background-size: 16px 16px;
        background-position: 0 0, 0 8px, 8px -8px, -8px 0;
      }

      .thumbnail {
        display: grid;
        gap: 8px;
        padding: 10px;
        border-radius: 16px;
        border: 1px solid var(--line);
        background: color-mix(in srgb, var(--paper-strong) 94%, transparent);
      }

      .thumbnail canvas {
        width: 100%;
        height: auto;
        border-radius: 12px;
        border: 1px solid var(--line);
        background: #fff;
      }

      .thumbnail.match {
        border-color: color-mix(in srgb, var(--ok) 50%, var(--line));
        box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--ok) 30%, transparent);
      }

      .thumbnail-meta {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 8px;
        font-size: 0.88rem;
      }

      .hidden {
        display: none;
      }

      @media (max-width: 980px) {
        .hero,
        .auth-grid,
        .preview-grid,
        .status-subgrid,
        .stats-grid {
          grid-template-columns: 1fr;
        }
      }

      @media (max-width: 760px) {
        .shell {
          padding: 12px;
        }

        .grid2,
        .grid3 {
          grid-template-columns: 1fr;
        }

        .tab-bar button {
          min-width: 0;
        }
      }
    </style>
  </head>
  <body>
    <div class="shell">
      <header class="hero">
        <section class="card stack">
          <div class="section-title">
            <div class="stack">
              <h1>Printo Admin</h1>
              <p class="muted">One print action in, correctly routed pages out: outgoing labels to thermal, invoices and return labels to A4.</p>
            </div>
          </div>
          <div class="hint muted">
            Every incoming page is classified automatically (outgoing shipping label, return label, or document) and routed by
            the rules in the profile. The preview below uses the same classifier the worker applies to future documents.
          </div>
        </section>
        <section class="card stack">
          <strong id="sessionLabel">Signed out</strong>
          <div id="sessionMeta" class="muted">Administrative login required.</div>
          <div id="topStatus" class="status muted"></div>
        </section>
      </header>

      <section id="authStage" class="auth-grid">
        <section id="bootstrapPanel" class="card stack hidden">
          <div class="section-title">
            <div class="stack">
              <h2>First admin setup</h2>
              <p class="muted">Shown only if the database has no users yet.</p>
            </div>
          </div>
          <form id="bootstrapForm" class="stack">
            <div class="grid2">
              <input name="username" placeholder="admin username" autocomplete="username" required />
              <input name="password" type="password" placeholder="admin password" autocomplete="new-password" required />
            </div>
            <input name="token" placeholder="bootstrap token if configured" />
            <button type="submit">Create administrator</button>
          </form>
          <div id="bootstrapStatus" class="status muted"></div>
        </section>

        <section class="card stack">
          <div class="section-title">
            <div class="stack">
              <h2>Administrator login</h2>
              <p class="muted">Only ADMIN users can access this panel.</p>
            </div>
          </div>
          <form id="loginForm" class="stack">
            <div class="grid2">
              <input name="username" placeholder="username" autocomplete="username" required />
              <input name="password" type="password" placeholder="password" autocomplete="current-password" required />
            </div>
            <div class="actions">
              <button type="submit">Sign in</button>
              <button id="logoutButton" class="secondary" type="button">Sign out</button>
            </div>
          </form>
          <div id="authStatus" class="status muted"></div>
        </section>
      </section>

      <section id="appStage" class="hidden">
        <nav class="tab-bar">
          <button type="button" class="active" data-tab="status">Status</button>
          <button type="button" data-tab="recognition">Routing profiles</button>
          <button type="button" data-tab="printers">Printers</button>
          <button type="button" data-tab="mappings">Mappings</button>
          <button id="refreshAllButton" type="button" class="secondary">Reload data</button>
        </nav>

        <div class="tab-panels">
          <section id="panel-recognition" class="panel">
            <div class="grid2">
              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Routing profile setup</h2>
                    <p class="muted">Decide where classified pages go. Test the rules against a real PDF before saving.</p>
                  </div>
                  <span class="pill" id="profileCount">0 profiles</span>
                </div>

                <form id="routingForm" class="stack">
                  <input name="id" type="hidden" />
                  <div class="grid2">
                    <label class="stack">
                      <span class="muted">Profile name</span>
                      <input name="name" placeholder="e.g. Standard shipping" required />
                    </label>
                    <label class="stack">
                      <span class="muted">Default route for unmatched pages</span>
                      <select name="defaultRouteType">
                        <option value="A4">A4 printer</option>
                        <option value="THERMAL">Thermal printer</option>
                      </select>
                    </label>
                  </div>

                  <section class="stack">
                    <div class="section-title">
                      <div class="stack">
                        <h3>Automatic label detection</h3>
                        <p class="muted">Each page is classified as an outgoing label, a return label, or a document. These rules map classes to printers.</p>
                      </div>
                    </div>
                    <div id="classificationRoutesEditor" class="stack"></div>
                    <div class="actions">
                      <button id="addClassificationRoute" class="ghost" type="button">Add detection rule</button>
                    </div>
                  </section>

                  <label class="stack">
                    <span class="muted">Thermal keyword patterns — one per line; pages whose text matches always go to thermal (beats classification)</span>
                    <textarea name="thermalLabelPatterns" placeholder="e.g. WAYBILL&#10;/^DHL EXPRESS/"></textarea>
                  </label>

                  <details class="card">
                    <summary><strong>Default printer credentials</strong> <span class="muted">Used by printers unless mapping or printer overrides them.</span></summary>
                    <div class="grid2" style="margin-top: 12px;">
                      <input name="printerDomainUsername" placeholder="DOMAIN\\user or user@domain" />
                      <input name="printerPassword" type="password" placeholder="password or secret override" />
                    </div>
                  </details>

                  <details class="card" id="snippetSection">
                    <summary><strong>Image snippet matching (advanced, optional)</strong> <span class="muted">Legacy pixel matching for documents the classifier cannot read.</span></summary>
                    <div class="snippet-box" style="margin-top: 12px;">
                      <img id="snippetPreview" class="snippet-preview" alt="Snippet preview" />
                      <input id="routingSnippetFile" type="file" accept="image/*" />
                      <div class="grid2">
                        <label class="stack">
                          <span class="muted">Match threshold</span>
                          <input id="matchThresholdRange" type="range" min="0.75" max="0.99" step="0.01" value="0.88" />
                        </label>
                        <label class="stack">
                          <span class="muted">Threshold value</span>
                          <input name="matchThreshold" id="matchThresholdInput" type="number" min="0.75" max="0.99" step="0.01" value="0.88" />
                        </label>
                      </div>
                      <div class="actions">
                        <button id="previewRoutingButton" class="secondary" type="button">Preview snippet matches</button>
                        <button id="clearSnippetButton" class="ghost" type="button">Clear snippet</button>
                      </div>
                    </div>
                  </details>

                  <div class="actions">
                    <button type="submit">Save profile</button>
                    <button id="resetRoutingForm" class="secondary" type="button">New profile</button>
                  </div>
                </form>
                <div id="routingStatus" class="status muted"></div>
              </section>

              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Test with a real document</h2>
                    <p class="muted">Upload a PDF a user would print. The preview runs the same classifier and rules the worker will apply.</p>
                  </div>
                </div>
                <input id="routingTestPdf" type="file" accept="application/pdf" />
                <div class="actions">
                  <button id="previewClassificationButton" type="button">Preview routing</button>
                  <button id="clearRoutingPreviewButton" class="secondary" type="button">Clear preview</button>
                </div>
                <div id="routingPreviewStatus" class="status muted"></div>
                <div id="routingPreviewSummary" class="hint muted">
                  Load a test PDF and press Preview routing to see how each page will be classified and where it will print.
                </div>
                <div id="routingPreviewPages" class="thumbnail-grid"></div>
              </section>
            </div>

            <section class="card stack">
              <div class="section-title">
                <div class="stack">
                  <h2>Saved profiles</h2>
                  <p class="muted">Pick one of these in the mapping tab for each watched input directory.</p>
                </div>
              </div>
              <div id="routingProfileList" class="list"></div>
            </section>
          </section>

          <section id="panel-printers" class="panel">
            <div class="grid2">
              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Printer setup</h2>
                    <p class="muted">Define A4 and thermal targets. Printer credentials override mapping and recognition defaults.</p>
                  </div>
                  <span class="pill" id="printerCount">0 printers</span>
                </div>

                <form id="printerForm" class="stack">
                  <input name="id" type="hidden" />
                  <div class="grid3">
                    <input name="name" placeholder="printer name" required />
                    <select name="type">
                      <option value="A4">A4</option>
                      <option value="THERMAL">THERMAL</option>
                    </select>
                    <input name="targetUri" placeholder="cups://Queue (recommended), \\\\printserver\\printer, ipp://host/printer, or socket://host:9100" required />
                  </div>
                  <div class="grid2">
                    <input name="domainUsername" placeholder="DOMAIN\\user or user@domain" />
                    <input name="password" type="password" placeholder="password or secret override" />
                  </div>
                  <label><input name="isActive" type="checkbox" checked /> Printer active</label>
                  <div class="actions">
                    <button type="submit">Save printer</button>
                    <button id="resetPrinterForm" class="secondary" type="button">New printer</button>
                  </div>
                </form>
                <div id="printerStatus" class="status muted"></div>
              </section>

              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Saved printers</h2>
                    <p class="muted">Only active printers are selectable for routing.</p>
                  </div>
                </div>
                <div id="printerList" class="list"></div>
              </section>
            </div>
          </section>

          <section id="panel-mappings" class="panel">
            <div class="grid2">
              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Input directory to printer mapping</h2>
                    <p class="muted">Each source uses one recognition profile, one A4 printer, and one thermal printer.</p>
                  </div>
                  <span class="pill" id="sourceCount">0 mappings</span>
                </div>

                <form id="sourceForm" class="stack">
                  <input name="id" type="hidden" />
                  <input name="path" placeholder="\\\\server\\share\\folder" required />
                  <div class="grid3">
                    <select id="sourceRoutingProfile" name="routingProfileId"></select>
                    <select id="sourceA4Printer" name="a4PrinterId"></select>
                    <select id="sourceThermalPrinter" name="thermalPrinterId"></select>
                  </div>
                  <details class="card" open>
                    <summary><strong>Input directory credentials</strong> <span class="muted">Used for SMB polling when the input path needs authentication.</span></summary>
                    <div class="grid2" style="margin-top: 12px;">
                      <input name="domainUsername" placeholder="DOMAIN\\user or user@domain" />
                      <input name="password" type="password" placeholder="SMB password or secret override" />
                    </div>
                  </details>
                  <details class="card">
                    <summary><strong>Printer credential override</strong> <span class="muted">Overrides recognition profile defaults for both mapped printers.</span></summary>
                    <div class="grid2" style="margin-top: 12px;">
                      <input name="printerDomainUsername" placeholder="DOMAIN\\user or user@domain" />
                      <input name="printerPassword" type="password" placeholder="password or secret override" />
                    </div>
                  </details>
                  <div class="grid2">
                    <textarea name="includeFilenamePatterns" placeholder="Optional include patterns, one per line"></textarea>
                    <textarea name="excludeFilenamePatterns" placeholder="Optional exclude patterns, one per line"></textarea>
                  </div>
                  <label><input name="isActive" type="checkbox" checked /> Mapping active</label>
                  <div class="actions">
                    <button type="submit">Save mapping</button>
                    <button id="resetSourceForm" class="secondary" type="button">New mapping</button>
                  </div>
                </form>
                <div id="sourceStatus" class="status muted"></div>
              </section>

              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Saved mappings</h2>
                    <p class="muted">These are the worker’s live source rules.</p>
                  </div>
                </div>
                <div id="sourceList" class="list"></div>
              </section>
            </div>
          </section>

          <section id="panel-status" class="panel active">
            <div class="status-layout">
              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Status</h2>
                    <p class="muted">Live worker, printer, mapping, and recognition profile signals. Printer states are derived from recent job activity.</p>
                  </div>
                  <span class="pill" id="statusUpdatedAt">Waiting for live data</span>
                </div>
                <div id="statusSummary" class="stats-grid"></div>
                <div id="statusHealthNote" class="hint muted">Live polling starts after configuration loads.</div>
              </section>

              <div class="status-subgrid">
                <section class="card stack">
                  <div class="section-title">
                    <div class="stack">
                      <h2>Mappings</h2>
                      <p class="muted">Recent source activity, configuration gaps, and dispatch outcomes.</p>
                    </div>
                  </div>
                  <div id="mappingStatusList" class="status-table"></div>
                </section>

                <section class="card stack">
                  <div class="section-title">
                    <div class="stack">
                      <h2>Printers</h2>
                      <p class="muted">Derived from active jobs and recent page dispatch results.</p>
                    </div>
                  </div>
                  <div id="printerStatusList" class="status-table"></div>
                </section>
              </div>

              <div class="status-subgrid">
                <section class="card stack">
                  <div class="section-title">
                    <div class="stack">
                      <h2>Recent Jobs</h2>
                      <p class="muted">Retry failed or partial jobs. Cancel only applies to stalled or failed jobs, not active in-flight dispatches.</p>
                    </div>
                  </div>
                  <div id="jobStatusList" class="log-list"></div>
                </section>

                <section class="card stack">
                  <div class="section-title">
                    <div class="stack">
                      <h2>Recent Logs</h2>
                      <p class="muted">Recent worker jobs and admin audit events.</p>
                    </div>
                  </div>
                  <div id="statusLogList" class="log-list"></div>
                </section>
              </div>

              <div class="status-subgrid">
                <section class="card stack">
                  <div class="section-title">
                    <div class="stack">
                      <h2>Routing Profiles</h2>
                      <p class="muted">Detection rules in use, mapping counts, and recent traffic.</p>
                    </div>
                  </div>
                  <div id="profileStatusList" class="status-table"></div>
                </section>

                <section class="card stack">
                  <div class="section-title">
                    <div class="stack">
                      <h2>Worker Controls</h2>
                      <p class="muted">The worker polls input folders on its own schedule; run it immediately after changing configuration or dropping a test file.</p>
                    </div>
                  </div>
                  <div class="actions">
                    <button id="runPipelineButton" type="button">Run pipeline now</button>
                  </div>
                  <div id="runPipelineStatus" class="status muted"></div>
                  <div class="hint muted">
                    Stalled jobs are pending jobs left behind while the worker runner is idle. Cancelling them prevents accidental automatic pickup; retrying them starts a targeted re-run of the unfinished work.
                  </div>
                </section>
              </div>
            </div>
          </section>
        </div>
      </section>
    </div>

    <script src="/vendor/pdfjs/pdf.js"></script>
    <script>
      (function () {
        const pdfjsLib = window.pdfjsLib;
        if (pdfjsLib && pdfjsLib.GlobalWorkerOptions) {
          pdfjsLib.GlobalWorkerOptions.workerSrc = '/vendor/pdfjs/pdf.worker.js';
        }

        const state = {
          token: localStorage.getItem('printo.admin.token') || '',
          user: null,
          activeTab: 'status',
          printers: [],
          sources: [],
          routingProfiles: [],
          live: {
            runner: null,
            jobs: [],
            jobPages: {},
            logs: [],
            vision: null,
            lastUpdatedAt: null,
            error: ''
          },
          recognition: {
            snippetBase64: null,
            samplePdfBase64: null,
            samplePdfName: '',
            previewMatches: [],
            classificationRoutes: []
          }
        };

        const PAGE_CLASS_OPTIONS = [
          { value: 'OUTGOING_LABEL_THERMAL', label: 'Outgoing shipping label' },
          { value: 'RETURN_LABEL_A4', label: 'Return label (customer copy)' },
          { value: 'DOCUMENT_A4', label: 'Document / invoice / other' }
        ];

        function pageClassLabel(value) {
          const option = PAGE_CLASS_OPTIONS.find(function (entry) { return entry.value === value; });
          return option ? option.label : (value || 'Unclassified');
        }

        function defaultClassificationRoutes() {
          return [
            { pageClass: 'OUTGOING_LABEL_THERMAL', routeType: 'THERMAL', printerId: null, minConfidence: 0.5 },
            { pageClass: 'RETURN_LABEL_A4', routeType: 'A4', printerId: null, minConfidence: 0.5 }
          ];
        }
        let livePollTimer = null;
        let liveRefreshInFlight = null;

        function byId(id) {
          return document.getElementById(id);
        }

        function field(form, name) {
          return form.elements.namedItem(name);
        }

        function escapeHtml(value) {
          return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
        }

        function splitLines(value) {
          return String(value || '')
            .split(/\\n|,/)
            .map(function (part) { return part.trim(); })
            .filter(Boolean);
        }

        function setStatus(id, message, kind) {
          const node = byId(id);
          node.textContent = message;
          node.className = 'status ' + (kind || 'muted');
        }

        function setTopStatus(message, kind) {
          setStatus('topStatus', message, kind);
        }

        async function request(path, method, body, extraHeaders) {
          const headers = Object.assign({ accept: 'application/json' }, extraHeaders || {});
          if (state.token) {
            headers.authorization = 'Bearer ' + state.token;
          }

          const init = { method: method, headers: headers };
          if (body !== undefined) {
            headers['content-type'] = 'application/json';
            init.body = JSON.stringify(body);
          }

          const response = await fetch(path, init);
          const contentType = response.headers.get('content-type') || '';
          const payload = contentType.includes('application/json') ? await response.json() : await response.text();
          if (!response.ok) {
            throw new Error(payload && payload.error ? payload.error : response.statusText || 'REQUEST_FAILED');
          }
          return payload;
        }

        function setSession(user) {
          state.user = user;
          const signedIn = Boolean(user && state.token);
          byId('sessionLabel').textContent = signedIn ? user.username + ' (admin)' : 'Signed out';
          byId('sessionMeta').textContent = signedIn ? 'Administrative access granted.' : 'Administrative login required.';
          byId('authStage').classList.toggle('hidden', signedIn);
          byId('appStage').classList.toggle('hidden', !signedIn);
        }

        function showTab(tabName) {
          state.activeTab = tabName;
          document.querySelectorAll('[data-tab]').forEach(function (button) {
            button.classList.toggle('active', button.getAttribute('data-tab') === tabName);
          });
          document.querySelectorAll('.panel').forEach(function (panel) {
            panel.classList.toggle('active', panel.id === 'panel-' + tabName);
          });
        }

        function setOptions(selectId, options, placeholder) {
          const select = byId(selectId);
          const current = select.value;
          const html = ['<option value="">' + escapeHtml(placeholder) + '</option>'].concat(options.map(function (option) {
            return '<option value="' + escapeHtml(option.value) + '">' + escapeHtml(option.label) + '</option>';
          }));
          select.innerHTML = html.join('');
          select.value = current;
          if (select.value !== current) {
            select.value = '';
          }
        }

        function syncSelects() {
          const a4Printers = state.printers.filter(function (printer) { return printer.type === 'A4'; }).map(function (printer) {
            return { value: printer.id, label: printer.name };
          });
          const thermalPrinters = state.printers.filter(function (printer) { return printer.type === 'THERMAL'; }).map(function (printer) {
            return { value: printer.id, label: printer.name };
          });
          const profiles = state.routingProfiles.map(function (profile) {
            return { value: profile.id, label: profile.name };
          });

          setOptions('sourceRoutingProfile', profiles, 'Select profile');
          setOptions('sourceA4Printer', a4Printers, 'Select A4 printer');
          setOptions('sourceThermalPrinter', thermalPrinters, 'Select thermal printer');
          renderClassificationRoutes();
        }

        function updateCounts() {
          byId('profileCount').textContent = state.routingProfiles.length + ' profile' + (state.routingProfiles.length === 1 ? '' : 's');
          byId('sourceCount').textContent = state.sources.length + ' mapping' + (state.sources.length === 1 ? '' : 's');
          byId('printerCount').textContent = state.printers.length + ' printer' + (state.printers.length === 1 ? '' : 's');
        }

        function credentialSummary(username, secretRef) {
          if (!username && !secretRef) {
            return 'inherits parent/default';
          }
          if (username && secretRef) {
            return username + ' with explicit secret';
          }
          if (username) {
            return username + ' (username only)';
          }
          return 'explicit secret only';
        }

        function printerName(printerId) {
          if (!printerId) {
            return 'Not set';
          }
          const printer = state.printers.find(function (entry) { return entry.id === printerId; });
          return printer ? printer.name : printerId;
        }

        function profileName(profileId) {
          if (!profileId) {
            return 'Not set';
          }
          const profile = state.routingProfiles.find(function (entry) { return entry.id === profileId; });
          return profile ? profile.name : profileId;
        }

        function formatDateTime(value) {
          if (!value) {
            return 'never';
          }
          const date = new Date(value);
          if (Number.isNaN(date.getTime())) {
            return String(value);
          }
          return new Intl.DateTimeFormat(undefined, {
            dateStyle: 'medium',
            timeStyle: 'short'
          }).format(date);
        }

        function formatRelativeTime(value) {
          if (!value) {
            return 'never';
          }
          const timestamp = new Date(value).getTime();
          if (!Number.isFinite(timestamp)) {
            return String(value);
          }
          const diffMs = timestamp - Date.now();
          const diffMinutes = Math.round(diffMs / 60000);
          const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

          if (Math.abs(diffMinutes) < 60) {
            return formatter.format(diffMinutes, 'minute');
          }

          const diffHours = Math.round(diffMinutes / 60);
          if (Math.abs(diffHours) < 48) {
            return formatter.format(diffHours, 'hour');
          }

          const diffDays = Math.round(diffHours / 24);
          return formatter.format(diffDays, 'day');
        }

        function toneForLabel(label) {
          if (label === 'ERROR' || label === 'MISCONFIGURED' || label === 'FAILURE' || label === 'PARTIAL_FAILURE') {
            return 'danger';
          }
          if (label === 'BUSY' || label === 'PROCESSING' || label === 'INCOMPLETE' || label === 'OFFLINE' || label === 'UNUSED' || label === 'CANCELLED' || label === 'PARTIAL_CANCELLED' || label === 'STALLED') {
            return 'warn';
          }
          if (label === 'ONLINE' || label === 'ACTIVE' || label === 'READY' || label === 'SUCCESS') {
            return 'ok';
          }
          return 'info';
        }

        function renderPill(label) {
          const tone = toneForLabel(label);
          return '<span class="pill ' + tone + '">' + escapeHtml(label) + '</span>';
        }

        function sourceById(sourceId) {
          return state.sources.find(function (entry) { return entry.id === sourceId; }) || null;
        }

        function jobProgress(job) {
          const pages = pagesForJob(job.id);
          return {
            success: pages.filter(function (page) { return page.status === 'SUCCESS'; }).length,
            failure: pages.filter(function (page) { return page.status === 'FAILURE'; }).length,
            skipped: pages.filter(function (page) { return page.status === 'SKIPPED'; }).length,
            total: pages.length
          };
        }

        function isJobStalled(job) {
          return job.status === 'PENDING' && !(state.live.runner && state.live.runner.isRunning);
        }

        function canRetryJob(job) {
          return job.status !== 'SUCCESS';
        }

        function canCancelJob(job) {
          return job.status === 'FAILURE' || isJobStalled(job);
        }

        function describeJobState(job) {
          const progress = jobProgress(job);
          const hasSuccessfulPages = progress.success > 0;
          const source = sourceById(job.sourceId);

          if (job.status === 'SUCCESS') {
            return {
              label: 'SUCCESS',
              detail: 'Completed without remaining work.',
              sourcePath: source ? source.path : job.sourceId
            };
          }

          if (job.status === 'CANCELLED') {
            return {
              label: hasSuccessfulPages ? 'PARTIAL_CANCELLED' : 'CANCELLED',
              detail: hasSuccessfulPages ? 'Some pages already dispatched; retry resumes only the missing work.' : 'Cancelled and excluded from automatic reprocessing until retried.',
              sourcePath: source ? source.path : job.sourceId
            };
          }

          if (job.status === 'FAILURE') {
            return {
              label: hasSuccessfulPages ? 'PARTIAL_FAILURE' : 'FAILURE',
              detail: hasSuccessfulPages ? 'Some pages succeeded before the failure. Retry resumes the incomplete pages only.' : (job.errorMessage || 'Worker run failed before completion.'),
              sourcePath: source ? source.path : job.sourceId
            };
          }

          return {
            label: isJobStalled(job) ? 'STALLED' : 'PENDING',
            detail: isJobStalled(job) ? 'This job is still pending while the runner is idle. You can cancel or retry it manually.' : 'Worker is still processing this job.',
            sourcePath: source ? source.path : job.sourceId
          };
        }

        function renderJobPageDetails(job) {
          const pages = pagesForJob(job.id);
          if (!pages.length) {
            return '';
          }

          const rows = pages.map(function (page) {
            const parts = ['<strong>Page ' + escapeHtml(String(page.pageNumber)) + '</strong>'];
            if (page.pageClass) {
              parts.push('<span class="pill ' + (page.pageClass === 'DOCUMENT_A4' ? '' : 'info') + '">' + escapeHtml(pageClassLabel(page.pageClass)) + '</span>');
            }
            if (page.carrier) {
              parts.push('<span class="pill ok">' + escapeHtml(page.carrier) + '</span>');
            }
            if (page.classificationConfidence != null) {
              parts.push('<span class="muted">' + escapeHtml(Math.round(Number(page.classificationConfidence) * 100) + '%') + '</span>');
            }
            parts.push('<span class="muted">→ ' + escapeHtml(page.routeType + (page.printerId ? ' · ' + printerName(page.printerId) : '')) + '</span>');
            parts.push(renderPill(page.status));
            if (page.errorMessage && page.status !== 'SKIPPED') {
              parts.push('<span class="muted">' + escapeHtml(page.errorMessage) + '</span>');
            }
            return '<div class="page-detail-row">' + parts.join('') + '</div>';
          }).join('');

          return '<details><summary class="muted">Page routing details (' + pages.length + ' page' + (pages.length === 1 ? '' : 's') + ')</summary>' +
            '<div class="page-detail-table" style="margin-top: 8px;">' + rows + '</div></details>';
        }

        function renderJobActionButton(job, action, label, enabled, tone, title) {
          return '<button type="button" class="' + escapeHtml(tone) + '" data-' + action + '-job="' + escapeHtml(job.id) + '"' +
            (enabled ? '' : ' disabled') +
            ' title="' + escapeHtml(title) + '">' +
            escapeHtml(label) +
          '</button>';
        }

        function printerById(printerId) {
          return state.printers.find(function (entry) { return entry.id === printerId; }) || null;
        }

        function profileById(profileId) {
          return state.routingProfiles.find(function (entry) { return entry.id === profileId; }) || null;
        }

        function jobsForSource(sourceId) {
          return state.live.jobs.filter(function (job) { return job.sourceId === sourceId; });
        }

        function pagesForJob(jobId) {
          return Array.isArray(state.live.jobPages[jobId]) ? state.live.jobPages[jobId] : [];
        }

        function pagesForPrinter(printerId) {
          return state.live.jobs.reduce(function (pages, job) {
            return pages.concat(pagesForJob(job.id).filter(function (page) {
              return page.printerId === printerId;
            }));
          }, []);
        }

        function describePrinterStatus(printer) {
          const relatedPages = pagesForPrinter(printer.id);
          const relatedSources = state.sources.filter(function (source) {
            return source.a4PrinterId === printer.id || source.thermalPrinterId === printer.id;
          });
          const pendingJobs = state.live.jobs.filter(function (job) {
            return job.status === 'PENDING' && relatedSources.some(function (source) { return source.id === job.sourceId; });
          });
          const hasFailure = relatedPages.some(function (page) { return page.status === 'FAILURE'; });
          const hasSuccess = relatedPages.some(function (page) { return page.status === 'SUCCESS'; });

          if (!printer.isActive) {
            return { label: 'OFFLINE', detail: 'Printer is disabled in configuration.' };
          }
          if (pendingJobs.length && state.live.runner && state.live.runner.isRunning) {
            return { label: 'BUSY', detail: pendingJobs.length + ' queued job' + (pendingJobs.length === 1 ? '' : 's') + ' currently resolving.' };
          }
          if (hasFailure) {
            return { label: 'ERROR', detail: 'Recent dispatch failure detected for this printer.' };
          }
          if (hasSuccess) {
            return { label: 'ONLINE', detail: 'Recent page dispatch succeeded.' };
          }
          return { label: 'IDLE', detail: 'Active, but no recent dispatch activity was recorded.' };
        }

        function describeSourceStatus(source) {
          const profile = profileById(source.routingProfileId);
          const a4Printer = printerById(source.a4PrinterId);
          const thermalPrinter = printerById(source.thermalPrinterId);
          const jobs = jobsForSource(source.id);
          const hasFailure = jobs.some(function (job) { return job.status === 'FAILURE' || job.status === 'CANCELLED'; });
          const hasPending = jobs.some(function (job) { return job.status === 'PENDING'; });
          const hasSuccess = jobs.some(function (job) { return job.status === 'SUCCESS'; });

          if (!source.isActive) {
            return { label: 'OFFLINE', detail: 'Mapping is disabled.' };
          }
          if (!profile || !a4Printer || !thermalPrinter) {
            return { label: 'MISCONFIGURED', detail: 'One or more linked resources are missing.' };
          }
          if (!a4Printer.isActive || !thermalPrinter.isActive) {
            return { label: 'MISCONFIGURED', detail: 'A mapped printer is disabled.' };
          }
          if (hasFailure) {
            return { label: 'ERROR', detail: 'Recent worker job failed for this mapping.' };
          }
          if (hasPending && state.live.runner && state.live.runner.isRunning) {
            return { label: 'PROCESSING', detail: 'Worker is actively processing files from this mapping.' };
          }
          if (hasSuccess) {
            return { label: 'ACTIVE', detail: 'Recent worker jobs completed successfully.' };
          }
          return { label: 'IDLE', detail: 'Configured correctly and waiting for new files.' };
        }

        function describeProfileStatus(profile) {
          const mappedSources = state.sources.filter(function (source) {
            return source.routingProfileId === profile.id && source.isActive;
          });
          const jobs = mappedSources.reduce(function (allJobs, source) {
            return allJobs.concat(jobsForSource(source.id));
          }, []);
          const hasFailure = jobs.some(function (job) { return job.status === 'FAILURE' || job.status === 'CANCELLED'; });
          const hasSuccess = jobs.some(function (job) { return job.status === 'SUCCESS'; });

          if (!mappedSources.length) {
            return { label: 'UNUSED', detail: 'No active mappings currently use this profile.' };
          }
          if (hasFailure) {
            return { label: 'ERROR', detail: 'Recent mapping activity reported a failure.' };
          }
          if (hasSuccess) {
            return { label: 'ACTIVE', detail: 'This profile was used successfully in recent jobs.' };
          }
          return { label: 'READY', detail: mappedSources.length + ' active mapping' + (mappedSources.length === 1 ? '' : 's') + ' assigned.' };
        }

        function resetLiveState() {
          state.live.runner = null;
          state.live.jobs = [];
          state.live.jobPages = {};
          state.live.logs = [];
          state.live.lastUpdatedAt = null;
          state.live.error = '';
        }

        function renderStatusPanel() {
          const summaryTarget = byId('statusSummary');
          const mappingsTarget = byId('mappingStatusList');
          const printersTarget = byId('printerStatusList');
          const profilesTarget = byId('profileStatusList');
          const jobsTarget = byId('jobStatusList');
          const logsTarget = byId('statusLogList');
          const updatedAt = byId('statusUpdatedAt');
          const note = byId('statusHealthNote');

          updatedAt.textContent = state.live.lastUpdatedAt ? 'Updated ' + formatRelativeTime(state.live.lastUpdatedAt) : 'Waiting for live data';

          if (state.live.error) {
            note.textContent = state.live.error;
            note.className = 'hint danger';
          } else {
            const runner = state.live.runner;
            const summary = runner && runner.lastSummary;
            note.textContent = runner
              ? 'Worker ' + (runner.isRunning ? 'is running now.' : 'is idle.') + ' Last completed run: ' + formatDateTime(runner.lastRunFinishedAt) + '.'
              : 'Live polling starts after configuration loads.';
            note.className = 'hint muted';
            if (summary) {
              note.textContent += ' Last run scanned ' + summary.sourcesScanned + ' sources, processed ' + summary.filesProcessed + ' files, and recorded ' + summary.failures + ' failures.';
            }
          }

          if (!state.printers.length && !state.sources.length && !state.routingProfiles.length) {
            summaryTarget.innerHTML = '<div class="item muted">No live status yet.</div>';
            mappingsTarget.innerHTML = '<div class="item muted">No mappings configured.</div>';
            printersTarget.innerHTML = '<div class="item muted">No printers configured.</div>';
            profilesTarget.innerHTML = '<div class="item muted">No recognition profiles configured.</div>';
            jobsTarget.innerHTML = '<div class="log-item muted">No jobs recorded yet.</div>';
            logsTarget.innerHTML = '<div class="log-item muted">No logs available.</div>';
            return;
          }

          const printerStatuses = state.printers.map(function (printer) {
            return { printer: printer, state: describePrinterStatus(printer) };
          });
          const sourceStatuses = state.sources.map(function (source) {
            return { source: source, state: describeSourceStatus(source) };
          });
          const profileStatuses = state.routingProfiles.map(function (profile) {
            return { profile: profile, state: describeProfileStatus(profile) };
          });
          const onlinePrinters = printerStatuses.filter(function (entry) { return entry.state.label === 'ONLINE'; }).length;
          const busyPrinters = printerStatuses.filter(function (entry) { return entry.state.label === 'BUSY'; }).length;
          const problematicMappings = sourceStatuses.filter(function (entry) { return entry.state.label === 'ERROR' || entry.state.label === 'MISCONFIGURED'; }).length;
          const activeProfiles = profileStatuses.filter(function (entry) { return entry.state.label === 'ACTIVE' || entry.state.label === 'READY'; }).length;
          const runner = state.live.runner;

          const vision = state.live.vision;
          const visionSummary = !vision
            ? { value: 'Unknown', note: 'Status unavailable' }
            : !vision.configured
              ? { value: 'Heuristics', note: 'Text rules only — no Vision Service configured' }
              : vision.healthy
                ? {
                    value: 'Vision OK',
                    note: vision.backends
                      ? ['rasterizer', 'barcodes', 'ocr'].filter(function (key) {
                          return vision.backends[key === 'rasterizer' ? 'pdf_rasterizer' : key];
                        }).join(' + ') || 'text heuristics only'
                      : 'Vision Service reachable'
                  }
                : { value: 'Fallback', note: 'Vision Service unreachable — using local text rules' };

          summaryTarget.innerHTML = [
            '<article class="stat-card"><span class="muted">Worker runs</span><strong>' + escapeHtml(String(runner ? runner.runCount : 0)) + '</strong><span class="muted">Last run ' + escapeHtml(formatRelativeTime(runner && runner.lastRunFinishedAt)) + '</span></article>',
            '<article class="stat-card"><span class="muted">Printers online / busy</span><strong>' + escapeHtml(String(onlinePrinters)) + ' / ' + escapeHtml(String(busyPrinters)) + '</strong><span class="muted">' + escapeHtml(String(state.printers.length)) + ' configured printers</span></article>',
            '<article class="stat-card"><span class="muted">Mappings with issues</span><strong>' + escapeHtml(String(problematicMappings)) + '</strong><span class="muted">' + escapeHtml(String(state.sources.length)) + ' total mappings</span></article>',
            '<article class="stat-card"><span class="muted">Label detection</span><strong>' + escapeHtml(visionSummary.value) + '</strong><span class="muted">' + escapeHtml(visionSummary.note) + '</span></article>',
            '<article class="stat-card"><span class="muted">Routing profiles ready</span><strong>' + escapeHtml(String(activeProfiles)) + '</strong><span class="muted">' + escapeHtml(String(state.routingProfiles.length)) + ' total profiles</span></article>'
          ].join('');

          mappingsTarget.innerHTML = sourceStatuses.length
            ? sourceStatuses.map(function (entry) {
              const source = entry.source;
              const sourceJobsCount = jobsForSource(source.id).length;
              return '<article class="item">' +
                '<div class="status-row">' +
                  '<div><h3>' + escapeHtml(source.path) + '</h3><p class="muted">Profile: ' + escapeHtml(profileName(source.routingProfileId)) + '</p></div>' +
                  renderPill(entry.state.label) +
                '</div>' +
                '<p>' + escapeHtml(entry.state.detail) + '</p>' +
                '<div class="status-meta">' +
                  '<span class="pill">' + escapeHtml('A4: ' + printerName(source.a4PrinterId)) + '</span>' +
                  '<span class="pill">' + escapeHtml('Thermal: ' + printerName(source.thermalPrinterId)) + '</span>' +
                  '<span class="pill">' + escapeHtml(sourceJobsCount + ' recent job' + (sourceJobsCount === 1 ? '' : 's')) + '</span>' +
                '</div>' +
              '</article>';
            }).join('')
            : '<div class="item muted">No mappings configured.</div>';

          printersTarget.innerHTML = printerStatuses.length
            ? printerStatuses.map(function (entry) {
              const printer = entry.printer;
              const pages = pagesForPrinter(printer.id);
              const successfulPages = pages.filter(function (page) { return page.status === 'SUCCESS'; }).length;
              const failedPages = pages.filter(function (page) { return page.status === 'FAILURE'; }).length;
              return '<article class="item">' +
                '<div class="status-row">' +
                  '<div><h3>' + escapeHtml(printer.name) + '</h3><p class="muted">' + escapeHtml(printer.type + ' · ' + printer.targetUri) + '</p></div>' +
                  renderPill(entry.state.label) +
                '</div>' +
                '<p>' + escapeHtml(entry.state.detail) + '</p>' +
                '<div class="status-meta">' +
                  '<span class="pill">' + escapeHtml(successfulPages + ' successful pages') + '</span>' +
                  '<span class="pill ' + (failedPages ? 'danger' : '') + '">' + escapeHtml(failedPages + ' failed pages') + '</span>' +
                  '<span class="pill">' + escapeHtml(printer.isActive ? 'enabled' : 'disabled') + '</span>' +
                '</div>' +
              '</article>';
            }).join('')
            : '<div class="item muted">No printers configured.</div>';

          profilesTarget.innerHTML = profileStatuses.length
            ? profileStatuses.map(function (entry) {
              const mappedSources = state.sources.filter(function (source) {
                return source.routingProfileId === entry.profile.id && source.isActive;
              }).length;
              const ruleCount = Array.isArray(entry.profile.classificationRoutes) && entry.profile.classificationRoutes.length
                ? entry.profile.classificationRoutes.length + ' custom rule' + (entry.profile.classificationRoutes.length === 1 ? '' : 's')
                : 'recommended rules';
              return '<article class="item">' +
                '<div class="status-row">' +
                  '<div><h3>' + escapeHtml(entry.profile.name) + '</h3><p class="muted">Default route ' + escapeHtml(entry.profile.defaultRouteType || 'A4') + '</p></div>' +
                  renderPill(entry.state.label) +
                '</div>' +
                '<p>' + escapeHtml(entry.state.detail) + '</p>' +
                '<div class="status-meta">' +
                  '<span class="pill">' + escapeHtml(mappedSources + ' active mapping' + (mappedSources === 1 ? '' : 's')) + '</span>' +
                  '<span class="pill">' + escapeHtml(ruleCount) + '</span>' +
                  (entry.profile.snippetBase64 ? '<span class="pill">image snippet</span>' : '') +
                '</div>' +
              '</article>';
            }).join('')
            : '<div class="item muted">No recognition profiles configured.</div>';

          jobsTarget.innerHTML = state.live.jobs.length
            ? state.live.jobs.slice(0, 10).map(function (job) {
              const progress = jobProgress(job);
              const stateInfo = describeJobState(job);
              const retryEnabled = canRetryJob(job);
              const cancelEnabled = canCancelJob(job);
              return '<article class="log-item job-card">' +
                '<header>' +
                  '<div><h3>' + escapeHtml(job.filePath) + '</h3><p class="muted">Source: ' + escapeHtml(stateInfo.sourcePath) + '</p></div>' +
                  renderPill(stateInfo.label) +
                '</header>' +
                '<p>' + escapeHtml(stateInfo.detail) + '</p>' +
                '<div class="job-meta">' +
                  '<span class="pill ok">' + escapeHtml(progress.success + ' success') + '</span>' +
                  '<span class="pill ' + (progress.failure ? 'danger' : '') + '">' + escapeHtml(progress.failure + ' failures') + '</span>' +
                  '<span class="pill info">' + escapeHtml(progress.skipped + ' skipped') + '</span>' +
                  '<span class="pill">' + escapeHtml('Status: ' + job.status) + '</span>' +
                '</div>' +
                (job.errorMessage ? '<p class="muted">Error: ' + escapeHtml(job.errorMessage) + '</p>' : '') +
                renderJobPageDetails(job) +
                '<div class="job-actions">' +
                  renderJobActionButton(job, 'retry', 'Retry job', retryEnabled, 'secondary', retryEnabled ? 'Re-run only unfinished work for this job.' : 'Successful jobs do not need a retry.') +
                  renderJobActionButton(job, 'cancel', 'Cancel job', cancelEnabled, 'ghost', cancelEnabled ? 'Mark this stalled or failed job as cancelled.' : 'Only stalled or failed jobs can be cancelled safely.') +
                '</div>' +
              '</article>';
            }).join('')
            : '<div class="log-item muted">No worker jobs recorded yet.</div>';

          const workerSnapshotLogs = state.live.jobs.slice(0, 8).map(function (job) {
            return {
              kind: 'worker',
              title: job.filePath,
              status: job.status,
              detail: 'Source: ' + (state.sources.find(function (source) { return source.id === job.sourceId; })?.path || job.sourceId),
              when: runner && (runner.lastRunFinishedAt || runner.lastRunStartedAt) ? formatDateTime(runner.lastRunFinishedAt || runner.lastRunStartedAt) : 'current snapshot'
            };
          });
          const auditLogs = state.live.logs.slice(0, 8).map(function (record) {
            return {
              kind: 'audit',
              title: record.action,
              status: record.status,
              detail: [record.actorUsername || 'system', record.targetType || 'unknown target'].join(' · '),
              when: formatDateTime(record.createdAt)
            };
          });
          const combinedLogs = workerSnapshotLogs.concat(auditLogs).slice(0, 12);

          logsTarget.innerHTML = combinedLogs.length
            ? combinedLogs.map(function (record) {
              return '<article class="log-item">' +
                '<header>' +
                  '<div><h3>' + escapeHtml(record.title) + '</h3><p class="muted">' + escapeHtml(record.detail) + '</p></div>' +
                  renderPill(record.status) +
                '</header>' +
                '<p class="muted">' + escapeHtml(record.kind === 'worker' ? 'Worker queue snapshot' : 'Admin audit log') + '</p>' +
                '<p>' + escapeHtml(record.when) + '</p>' +
              '</article>';
            }).join('')
            : '<div class="log-item muted">No worker jobs or admin audit logs recorded yet.</div>';
        }

        async function refreshLiveStatus() {
          if (!state.token) {
            return;
          }
          if (liveRefreshInFlight) {
            return liveRefreshInFlight;
          }

          liveRefreshInFlight = (async function () {
            try {
              const liveResults = await Promise.all([
                request('/worker/pipeline/status', 'GET'),
                request('/worker/pipeline/jobs?limit=20', 'GET'),
                request('/admin/logs?limit=20', 'GET'),
                request('/worker/pipeline/vision-status', 'GET').catch(function () { return null; })
              ]);
              const jobs = Array.isArray(liveResults[1]) ? liveResults[1] : [];
              const pageEntries = await Promise.all(jobs.map(async function (job) {
                const pages = await request('/worker/pipeline/jobs/' + job.id + '/pages', 'GET');
                return [job.id, Array.isArray(pages) ? pages : []];
              }));
              state.live.runner = liveResults[0] && liveResults[0].runner ? liveResults[0].runner : null;
              state.live.jobs = jobs;
              state.live.jobPages = Object.fromEntries(pageEntries);
              state.live.logs = Array.isArray(liveResults[2]) ? liveResults[2] : [];
              state.live.vision = liveResults[3];
              state.live.lastUpdatedAt = new Date().toISOString();
              state.live.error = '';
            } catch (error) {
              state.live.error = 'Live status is unavailable: ' + String(error.message || error);
            } finally {
              renderStatusPanel();
              liveRefreshInFlight = null;
            }
          })();

          return liveRefreshInFlight;
        }

        function stopLivePolling() {
          if (livePollTimer) {
            clearInterval(livePollTimer);
            livePollTimer = null;
          }
        }

        function startLivePolling() {
          stopLivePolling();
          if (!state.token) {
            return;
          }
          void refreshLiveStatus();
          livePollTimer = setInterval(function () {
            void refreshLiveStatus();
          }, 10000);
        }

        function renderClassificationRoutes() {
          const target = byId('classificationRoutesEditor');
          const routes = state.recognition.classificationRoutes;
          if (!routes.length) {
            target.innerHTML = '<div class="item muted">No detection rules. Pages will follow the default route only. Add a rule or reset to the recommended setup.</div>';
            return;
          }

          const printerOptions = state.printers.filter(function (printer) { return printer.isActive; });
          target.innerHTML = routes.map(function (route, index) {
            const classOptions = PAGE_CLASS_OPTIONS.map(function (option) {
              return '<option value="' + option.value + '"' + (route.pageClass === option.value ? ' selected' : '') + '>' + escapeHtml(option.label) + '</option>';
            }).join('');
            const printerChoices = ['<option value="">Auto (first ' + escapeHtml(route.routeType) + ' printer)</option>'].concat(
              printerOptions.map(function (printer) {
                return '<option value="' + escapeHtml(printer.id) + '"' + (route.printerId === printer.id ? ' selected' : '') + '>' +
                  escapeHtml(printer.name + ' (' + printer.type + ')') + '</option>';
              })
            ).join('');
            return '<div class="route-rule" data-route-index="' + index + '">' +
              '<label>When page is<select data-route-field="pageClass">' + classOptions + '</select></label>' +
              '<label>Send to<select data-route-field="routeType">' +
                '<option value="THERMAL"' + (route.routeType === 'THERMAL' ? ' selected' : '') + '>Thermal</option>' +
                '<option value="A4"' + (route.routeType === 'A4' ? ' selected' : '') + '>A4</option>' +
              '</select></label>' +
              '<label>Printer<select data-route-field="printerId">' + printerChoices + '</select></label>' +
              '<label>Min conf.<input data-route-field="minConfidence" type="number" min="0" max="1" step="0.05" value="' + escapeHtml(String(route.minConfidence)) + '" /></label>' +
              '<button type="button" class="ghost" data-remove-route="' + index + '" title="Remove this rule">✕</button>' +
            '</div>';
          }).join('');
        }

        function readClassificationRoutesPayload() {
          return state.recognition.classificationRoutes.map(function (route) {
            const minConfidence = Number(route.minConfidence);
            return {
              pageClass: route.pageClass,
              routeType: route.routeType === 'THERMAL' ? 'THERMAL' : 'A4',
              printerId: route.printerId || null,
              minConfidence: Number.isFinite(minConfidence) ? Math.min(1, Math.max(0, minConfidence)) : 0.5
            };
          });
        }

        function renderSnippetPreview() {
          const image = byId('snippetPreview');
          if (!state.recognition.snippetBase64) {
            image.removeAttribute('src');
            return;
          }
          image.src = 'data:image/png;base64,' + state.recognition.snippetBase64;
        }

        function renderPrinters() {
          const target = byId('printerList');
          if (!state.printers.length) {
            target.innerHTML = '<div class="item muted">No printers configured.</div>';
            return;
          }

          target.innerHTML = state.printers.map(function (printer) {
            return '<article class="item">' +
              '<header>' +
                '<div><h3>' + escapeHtml(printer.name) + '</h3><p class="muted">' + escapeHtml(printer.type) + '</p></div>' +
                '<span class="pill">' + escapeHtml(printer.isActive ? 'ACTIVE' : 'INACTIVE') + '</span>' +
              '</header>' +
              '<p><strong>Target:</strong> ' + escapeHtml(printer.targetUri) + '</p>' +
              '<p><strong>Credentials:</strong> ' + escapeHtml(credentialSummary(printer.domainUsername, printer.secretRef)) + '</p>' +
              '<div class="actions">' +
                '<button type="button" class="secondary" data-edit-printer="' + escapeHtml(printer.id) + '">Edit</button>' +
                '<button type="button" class="ghost" data-delete-printer="' + escapeHtml(printer.id) + '">Delete</button>' +
              '</div>' +
            '</article>';
          }).join('');
        }

        function describeClassificationRoutes(profile) {
          const routes = Array.isArray(profile.classificationRoutes) && profile.classificationRoutes.length
            ? profile.classificationRoutes
            : defaultClassificationRoutes();
          return routes.map(function (route) {
            const printer = route.printerId ? printerName(route.printerId) : 'auto';
            return pageClassLabel(route.pageClass) + ' → ' + route.routeType + ' (' + printer + ', ≥' + Math.round((route.minConfidence || 0) * 100) + '%)';
          });
        }

        function renderProfiles() {
          const target = byId('routingProfileList');
          if (!state.routingProfiles.length) {
            target.innerHTML = '<div class="item muted">No routing profiles configured.</div>';
            return;
          }

          target.innerHTML = state.routingProfiles.map(function (profile) {
            const routeSummaries = describeClassificationRoutes(profile);
            const usingDefaults = !(Array.isArray(profile.classificationRoutes) && profile.classificationRoutes.length);
            const patterns = Array.isArray(profile.thermalLabelPatterns) ? profile.thermalLabelPatterns : [];
            return '<article class="item">' +
              '<header>' +
                '<div><h3>' + escapeHtml(profile.name) + '</h3><p class="muted">Default route: ' + escapeHtml(profile.defaultRouteType || 'A4') + '</p></div>' +
                '<span class="pill ' + (usingDefaults ? '' : 'info') + '">' + (usingDefaults ? 'recommended rules' : 'custom rules') + '</span>' +
              '</header>' +
              '<p><strong>Label detection:</strong></p>' +
              routeSummaries.map(function (line) { return '<p class="muted">' + escapeHtml(line) + '</p>'; }).join('') +
              '<p><strong>Thermal keywords:</strong> ' + escapeHtml(patterns.length ? patterns.join(', ') : 'none') + '</p>' +
              '<p><strong>Image snippet:</strong> ' + escapeHtml(profile.snippetBase64 ? 'configured (threshold ' + String(profile.matchThreshold || 0.88) + ')' : 'not used') + '</p>' +
              '<p><strong>Printer credentials:</strong> ' + escapeHtml(credentialSummary(profile.printerDomainUsername, profile.printerSecretRef)) + '</p>' +
              '<div class="actions">' +
                '<button type="button" class="secondary" data-edit-profile="' + escapeHtml(profile.id) + '">Edit</button>' +
                '<button type="button" class="ghost" data-delete-profile="' + escapeHtml(profile.id) + '">Delete</button>' +
              '</div>' +
            '</article>';
          }).join('');
        }

        function renderSources() {
          const target = byId('sourceList');
          if (!state.sources.length) {
            target.innerHTML = '<div class="item muted">No input mappings configured.</div>';
            return;
          }

          target.innerHTML = state.sources.map(function (source) {
            const includes = Array.isArray(source.includeFilenamePatterns) && source.includeFilenamePatterns.length
              ? source.includeFilenamePatterns.join(', ')
              : 'none';
            const excludes = Array.isArray(source.excludeFilenamePatterns) && source.excludeFilenamePatterns.length
              ? source.excludeFilenamePatterns.join(', ')
              : 'none';
            return '<article class="item">' +
              '<header>' +
                '<div><h3>' + escapeHtml(source.path) + '</h3><p class="muted">Profile: ' + escapeHtml(profileName(source.routingProfileId)) + '</p></div>' +
                '<span class="pill">' + escapeHtml(source.isActive ? 'ACTIVE' : 'INACTIVE') + '</span>' +
              '</header>' +
              '<p><strong>A4:</strong> ' + escapeHtml(printerName(source.a4PrinterId)) + '</p>' +
              '<p><strong>Thermal:</strong> ' + escapeHtml(printerName(source.thermalPrinterId)) + '</p>' +
              '<p><strong>Input credentials:</strong> ' + escapeHtml(credentialSummary(source.domainUsername, source.secretRef)) + '</p>' +
              '<p><strong>Printer override:</strong> ' + escapeHtml(credentialSummary(source.printerDomainUsername, source.printerSecretRef)) + '</p>' +
              '<p><strong>Include:</strong> ' + escapeHtml(includes) + '</p>' +
              '<p><strong>Exclude:</strong> ' + escapeHtml(excludes) + '</p>' +
              '<div class="actions">' +
                '<button type="button" class="secondary" data-edit-source="' + escapeHtml(source.id) + '">Edit</button>' +
                '<button type="button" class="ghost" data-delete-source="' + escapeHtml(source.id) + '">Delete</button>' +
              '</div>' +
            '</article>';
          }).join('');
        }

        async function readFileBase64(file) {
          return await new Promise(function (resolve, reject) {
            const reader = new FileReader();
            reader.onload = function () {
              const result = String(reader.result || '');
              const commaIndex = result.indexOf(',');
              resolve(commaIndex >= 0 ? result.slice(commaIndex + 1) : result);
            };
            reader.onerror = function () {
              reject(reader.error || new Error('FILE_READ_FAILED'));
            };
            reader.readAsDataURL(file);
          });
        }

        async function renderPdfPreview(pdfBase64, annotations) {
          const target = byId('routingPreviewPages');
          target.innerHTML = '';
          if (!pdfBase64) {
            return;
          }

          const bytes = Uint8Array.from(atob(pdfBase64), function (char) { return char.charCodeAt(0); });
          const pdf = await pdfjsLib.getDocument({ data: bytes }).promise;

          try {
            for (let pageNumber = 1; pageNumber <= pdf.numPages; pageNumber += 1) {
              const page = await pdf.getPage(pageNumber);
              const viewport = page.getViewport({ scale: 0.32 });
              const card = document.createElement('article');
              const annotation = annotations.find(function (entry) { return entry.pageNumber === pageNumber; });
              card.className = 'thumbnail' + (annotation && annotation.routeType === 'THERMAL' ? ' match' : '');

              const canvas = document.createElement('canvas');
              canvas.width = Math.round(viewport.width);
              canvas.height = Math.round(viewport.height);
              const context = canvas.getContext('2d');
              await page.render({ canvasContext: context, viewport: viewport }).promise;

              const meta = document.createElement('div');
              meta.className = 'thumbnail-meta';
              meta.innerHTML =
                '<strong>Page ' + pageNumber + '</strong>' +
                '<span class="pill ' + (annotation && annotation.routeType === 'THERMAL' ? 'info' : '') + '">' +
                escapeHtml(annotation ? annotation.routeType : '?') + '</span>';

              const tags = document.createElement('div');
              tags.className = 'thumbnail-tags';
              tags.innerHTML = (annotation && annotation.tags ? annotation.tags : []).map(function (tag) {
                return '<span class="pill ' + escapeHtml(tag.tone || '') + '">' + escapeHtml(tag.label) + '</span>';
              }).join('');

              const note = document.createElement('div');
              note.className = 'muted';
              note.textContent = annotation && annotation.note ? annotation.note : '';

              card.appendChild(canvas);
              card.appendChild(meta);
              if (tags.innerHTML) {
                card.appendChild(tags);
              }
              if (note.textContent) {
                card.appendChild(note);
              }
              target.appendChild(card);
            }
          } finally {
            if (pdf.destroy) {
              await pdf.destroy();
            }
          }
        }

        function classificationAnnotations(pages) {
          return pages.map(function (page) {
            const tags = [{ label: pageClassLabel(page.pageClass), tone: page.pageClass === 'DOCUMENT_A4' ? '' : 'info' }];
            if (page.carrier) {
              tags.push({ label: page.carrier, tone: 'ok' });
            }
            if (!page.hasTextLayer) {
              tags.push({ label: 'scanned page', tone: 'warn' });
            }
            const percent = Math.round(Number(page.confidence || 0) * 100);
            return {
              pageNumber: page.pageNumber,
              routeType: page.routeType,
              tags: tags,
              note: percent + '% confidence · decided by ' + String(page.decidedBy || '').replace(/_/g, ' ').toLowerCase()
            };
          });
        }

        function snippetAnnotations(matches) {
          return matches.map(function (match) {
            return {
              pageNumber: match.pageNumber,
              routeType: match.isMatch ? 'THERMAL' : 'A4',
              tags: [{ label: 'snippet score ' + Number(match.score).toFixed(3), tone: match.isMatch ? 'ok' : '' }],
              note: ''
            };
          });
        }

        function syncThresholdInputs(value) {
          byId('matchThresholdRange').value = value;
          byId('matchThresholdInput').value = value;
        }

        function resetPrinterForm() {
          const form = byId('printerForm');
          form.reset();
          field(form, 'id').value = '';
          field(form, 'domainUsername').value = '';
          field(form, 'password').value = '';
          field(form, 'isActive').checked = true;
          setStatus('printerStatus', 'Create an A4 printer and a thermal printer before mapping sources.', 'muted');
        }

        function fillPrinterForm(printer) {
          const form = byId('printerForm');
          field(form, 'id').value = printer.id;
          field(form, 'name').value = printer.name;
          field(form, 'type').value = printer.type;
          field(form, 'targetUri').value = printer.targetUri;
          field(form, 'domainUsername').value = printer.domainUsername || '';
          field(form, 'password').value = '';
          field(form, 'isActive').checked = Boolean(printer.isActive);
          setStatus('printerStatus', 'Editing printer ' + printer.name + '.', 'muted');
          showTab('printers');
        }

        function clearRoutingPreview() {
          state.recognition.previewMatches = [];
          byId('routingPreviewPages').innerHTML = '';
          setStatus('routingPreviewStatus', 'Preview cleared.', 'muted');
          byId('routingPreviewSummary').textContent = 'Load a test PDF and press Preview routing to see how each page will be classified and where it will print.';
        }

        function resetRoutingForm() {
          const form = byId('routingForm');
          form.reset();
          field(form, 'id').value = '';
          field(form, 'defaultRouteType').value = 'A4';
          field(form, 'thermalLabelPatterns').value = '';
          field(form, 'printerDomainUsername').value = '';
          field(form, 'printerPassword').value = '';
          syncThresholdInputs('0.88');
          state.recognition.snippetBase64 = null;
          state.recognition.samplePdfBase64 = null;
          state.recognition.samplePdfName = '';
          state.recognition.classificationRoutes = defaultClassificationRoutes();
          renderClassificationRoutes();
          renderSnippetPreview();
          clearRoutingPreview();
          setStatus('routingStatus', 'Recommended setup preloaded: outgoing labels print on thermal, everything else on A4.', 'muted');
        }

        function fillRoutingForm(profile) {
          const form = byId('routingForm');
          field(form, 'id').value = profile.id;
          field(form, 'name').value = profile.name || '';
          field(form, 'defaultRouteType').value = profile.defaultRouteType === 'THERMAL' ? 'THERMAL' : 'A4';
          field(form, 'thermalLabelPatterns').value = Array.isArray(profile.thermalLabelPatterns) ? profile.thermalLabelPatterns.join('\\n') : '';
          field(form, 'printerDomainUsername').value = profile.printerDomainUsername || '';
          field(form, 'printerPassword').value = '';
          syncThresholdInputs(String(profile.matchThreshold || 0.88));
          state.recognition.snippetBase64 = profile.snippetBase64 || null;
          state.recognition.samplePdfBase64 = profile.samplePdfBase64 || null;
          state.recognition.samplePdfName = profile.samplePdfName || '';
          state.recognition.classificationRoutes = Array.isArray(profile.classificationRoutes) && profile.classificationRoutes.length
            ? profile.classificationRoutes.map(function (route) { return Object.assign({}, route); })
            : defaultClassificationRoutes();
          renderClassificationRoutes();
          renderSnippetPreview();
          byId('snippetSection').open = Boolean(profile.snippetBase64);
          byId('routingPreviewPages').innerHTML = '';
          setStatus('routingStatus', 'Editing routing profile ' + profile.name + '.', 'muted');
          setStatus('routingPreviewStatus', 'Load a test PDF and press Preview routing to test this profile.', 'muted');
          showTab('recognition');
        }

        function resetSourceForm() {
          const form = byId('sourceForm');
          form.reset();
          field(form, 'id').value = '';
          field(form, 'routingProfileId').value = '';
          field(form, 'a4PrinterId').value = '';
          field(form, 'thermalPrinterId').value = '';
          field(form, 'domainUsername').value = '';
          field(form, 'password').value = '';
          field(form, 'printerDomainUsername').value = '';
          field(form, 'printerPassword').value = '';
          field(form, 'isActive').checked = true;
          setStatus('sourceStatus', 'Map an input directory to one profile, one A4 printer, and one thermal printer.', 'muted');
        }

        function fillSourceForm(source) {
          const form = byId('sourceForm');
          field(form, 'id').value = source.id;
          field(form, 'path').value = source.path || '';
          field(form, 'routingProfileId').value = source.routingProfileId || '';
          field(form, 'a4PrinterId').value = source.a4PrinterId || '';
          field(form, 'thermalPrinterId').value = source.thermalPrinterId || '';
          field(form, 'domainUsername').value = source.domainUsername || '';
          field(form, 'password').value = '';
          field(form, 'printerDomainUsername').value = source.printerDomainUsername || '';
          field(form, 'printerPassword').value = '';
          field(form, 'includeFilenamePatterns').value = Array.isArray(source.includeFilenamePatterns) ? source.includeFilenamePatterns.join('\\n') : '';
          field(form, 'excludeFilenamePatterns').value = Array.isArray(source.excludeFilenamePatterns) ? source.excludeFilenamePatterns.join('\\n') : '';
          field(form, 'isActive').checked = Boolean(source.isActive);
          setStatus('sourceStatus', 'Editing mapping for ' + source.path + '.', 'muted');
          showTab('mappings');
        }

        async function loadBootstrapState() {
          try {
            const result = await request('/auth/bootstrap-status', 'GET');
            byId('bootstrapPanel').classList.toggle('hidden', !result.requiresBootstrap);
          } catch (error) {
            setTopStatus(String(error.message || error), 'danger');
          }
        }

        async function refreshAll() {
          const results = await Promise.all([
            request('/admin/config/printers', 'GET'),
            request('/admin/config/routing-profiles', 'GET'),
            request('/admin/config/smb-sources', 'GET')
          ]);

          state.printers = results[0];
          state.routingProfiles = results[1];
          state.sources = results[2];
          syncSelects();
          updateCounts();
          renderPrinters();
          renderProfiles();
          renderSources();
          try {
            await refreshLiveStatus();
          } catch (_error) {}
          setTopStatus('Configuration loaded.', 'ok');
        }

        byId('bootstrapForm').addEventListener('submit', async function (event) {
          event.preventDefault();
          const form = event.currentTarget;
          const token = field(form, 'token').value.trim();

          try {
            await request('/auth/bootstrap-admin', 'POST', {
              username: field(form, 'username').value.trim(),
              password: field(form, 'password').value
            }, token ? { 'x-bootstrap-token': token } : undefined);
            form.reset();
            setStatus('bootstrapStatus', 'Administrator created. You can sign in now.', 'ok');
            await loadBootstrapState();
          } catch (error) {
            setStatus('bootstrapStatus', String(error.message || error), 'danger');
          }
        });

        byId('loginForm').addEventListener('submit', async function (event) {
          event.preventDefault();
          const form = event.currentTarget;
          try {
            const result = await request('/auth/login', 'POST', {
              username: field(form, 'username').value.trim(),
              password: field(form, 'password').value
            });
            state.token = result.accessToken;
            localStorage.setItem('printo.admin.token', state.token);
            setSession(result.user);
            setStatus('authStatus', 'Signed in.', 'ok');
            await refreshAll();
            showTab('status');
            startLivePolling();
          } catch (error) {
            state.token = '';
            localStorage.removeItem('printo.admin.token');
            setSession(null);
            setStatus('authStatus', String(error.message || error), 'danger');
          }
        });

        byId('logoutButton').addEventListener('click', function () {
          stopLivePolling();
          state.token = '';
          localStorage.removeItem('printo.admin.token');
          resetLiveState();
          renderStatusPanel();
          setSession(null);
          setTopStatus('Signed out.', 'muted');
        });

        byId('refreshAllButton').addEventListener('click', async function () {
          try {
            await refreshAll();
            showTab('status');
          } catch (error) {
            setTopStatus(String(error.message || error), 'danger');
          }
        });

        byId('routingSnippetFile').addEventListener('change', async function (event) {
          const file = event.target.files && event.target.files[0];
          if (!file) {
            return;
          }

          try {
            state.recognition.snippetBase64 = await readFileBase64(file);
            renderSnippetPreview();
            setStatus('routingPreviewStatus', 'Snippet image loaded.', 'ok');
          } catch (error) {
            setStatus('routingPreviewStatus', String(error.message || error), 'danger');
          }
        });

        byId('routingTestPdf').addEventListener('change', async function (event) {
          const file = event.target.files && event.target.files[0];
          if (!file) {
            return;
          }

          try {
            state.recognition.samplePdfBase64 = await readFileBase64(file);
            state.recognition.samplePdfName = file.name;
            clearRoutingPreview();
            setStatus('routingPreviewStatus', 'Test PDF loaded. Run preview to see matches.', 'ok');
          } catch (error) {
            setStatus('routingPreviewStatus', String(error.message || error), 'danger');
          }
        });

        byId('matchThresholdRange').addEventListener('input', function (event) {
          syncThresholdInputs(event.target.value);
        });
        byId('matchThresholdInput').addEventListener('input', function (event) {
          syncThresholdInputs(event.target.value);
        });

        byId('clearSnippetButton').addEventListener('click', function () {
          state.recognition.snippetBase64 = null;
          renderSnippetPreview();
          setStatus('routingPreviewStatus', 'Snippet image cleared.', 'muted');
        });

        byId('clearRoutingPreviewButton').addEventListener('click', clearRoutingPreview);

        byId('previewRoutingButton').addEventListener('click', async function () {
          if (!state.recognition.snippetBase64) {
            setStatus('routingPreviewStatus', 'Load a snippet image first.', 'danger');
            return;
          }
          if (!state.recognition.samplePdfBase64) {
            setStatus('routingPreviewStatus', 'Load a PDF to preview matches.', 'danger');
            return;
          }

          try {
            setStatus('routingPreviewStatus', 'Matching pages...', 'muted');
            const result = await request('/admin/preview/routing-match', 'POST', {
              snippetBase64: state.recognition.snippetBase64,
              pdfBase64: state.recognition.samplePdfBase64,
              matchThreshold: Number(byId('matchThresholdInput').value || '0.88')
            });
            state.recognition.previewMatches = result.pages || [];
            const matchedPages = state.recognition.previewMatches.filter(function (page) { return page.isMatch; }).map(function (page) { return page.pageNumber; });
            byId('routingPreviewSummary').textContent =
              matchedPages.length > 0
                ? 'Snippet-matched pages route to THERMAL: ' + matchedPages.join(', ') + '. All other pages follow classification and default rules.'
                : 'No pages matched the snippet at the current threshold.';
            await renderPdfPreview(state.recognition.samplePdfBase64, snippetAnnotations(state.recognition.previewMatches));
            setStatus('routingPreviewStatus', 'Snippet preview ready.', 'ok');
          } catch (error) {
            setStatus('routingPreviewStatus', String(error.message || error), 'danger');
          }
        });

        byId('previewClassificationButton').addEventListener('click', async function () {
          if (!state.recognition.samplePdfBase64) {
            setStatus('routingPreviewStatus', 'Load a test PDF first.', 'danger');
            return;
          }

          const form = byId('routingForm');
          try {
            setStatus('routingPreviewStatus', 'Classifying pages...', 'muted');
            const result = await request('/worker/pipeline/preview/classification', 'POST', {
              pdfBase64: state.recognition.samplePdfBase64,
              profile: {
                defaultRouteType: field(form, 'defaultRouteType').value === 'THERMAL' ? 'THERMAL' : 'A4',
                thermalLabelPatterns: splitLines(field(form, 'thermalLabelPatterns').value),
                classificationRoutes: readClassificationRoutesPayload()
              }
            });
            const pages = Array.isArray(result.pages) ? result.pages : [];
            const thermalPages = pages.filter(function (page) { return page.routeType === 'THERMAL'; }).map(function (page) { return page.pageNumber; });
            const scannedPages = pages.filter(function (page) { return !page.hasTextLayer; }).length;
            byId('routingPreviewSummary').textContent =
              (thermalPages.length
                ? 'Pages ' + thermalPages.join(', ') + ' print on thermal; the rest print on A4.'
                : 'All pages print on A4 with the current rules.') +
              (scannedPages ? ' ' + scannedPages + ' page(s) have no text layer — accuracy for those depends on the Vision Service.' : '');
            await renderPdfPreview(state.recognition.samplePdfBase64, classificationAnnotations(pages));
            setStatus('routingPreviewStatus', 'Routing preview ready.', 'ok');
          } catch (error) {
            setStatus('routingPreviewStatus', String(error.message || error), 'danger');
          }
        });

        byId('classificationRoutesEditor').addEventListener('change', function (event) {
          const row = event.target.closest('[data-route-index]');
          const fieldName = event.target.getAttribute('data-route-field');
          if (!row || !fieldName) {
            return;
          }
          const index = Number(row.getAttribute('data-route-index'));
          const route = state.recognition.classificationRoutes[index];
          if (!route) {
            return;
          }
          if (fieldName === 'minConfidence') {
            route.minConfidence = Math.min(1, Math.max(0, Number(event.target.value) || 0));
          } else if (fieldName === 'printerId') {
            route.printerId = event.target.value || null;
          } else {
            route[fieldName] = event.target.value;
          }
          if (fieldName === 'routeType') {
            // Printer choices show the route type in the "auto" option; re-render to keep them honest.
            renderClassificationRoutes();
          }
        });

        byId('classificationRoutesEditor').addEventListener('click', function (event) {
          const removeButton = event.target.closest('[data-remove-route]');
          if (!removeButton) {
            return;
          }
          state.recognition.classificationRoutes.splice(Number(removeButton.getAttribute('data-remove-route')), 1);
          renderClassificationRoutes();
        });

        byId('addClassificationRoute').addEventListener('click', function () {
          state.recognition.classificationRoutes.push({
            pageClass: 'OUTGOING_LABEL_THERMAL',
            routeType: 'THERMAL',
            printerId: null,
            minConfidence: 0.5
          });
          renderClassificationRoutes();
        });

        byId('routingForm').addEventListener('submit', async function (event) {
          event.preventDefault();
          const form = event.currentTarget;
          const id = field(form, 'id').value.trim();
          const payload = {
            name: field(form, 'name').value.trim(),
            printerDomainUsername: field(form, 'printerDomainUsername').value.trim(),
            defaultRouteType: field(form, 'defaultRouteType').value === 'THERMAL' ? 'THERMAL' : 'A4',
            thermalLabelPatterns: splitLines(field(form, 'thermalLabelPatterns').value),
            classificationRoutes: readClassificationRoutesPayload(),
            fallbackPrinterId: null,
            samplePdfName: state.recognition.samplePdfName || null,
            samplePdfBase64: state.recognition.samplePdfBase64 || null,
            snippetBase64: state.recognition.snippetBase64 || null,
            matchThreshold: Number(field(form, 'matchThreshold').value || '0.88'),
            visualRules: []
          };
          const printerPassword = field(form, 'printerPassword').value;
          if (printerPassword) {
            payload.printerPassword = printerPassword;
          }

          try {
            await (id
              ? request('/admin/config/routing-profiles/' + id, 'PATCH', payload)
              : request('/admin/config/routing-profiles', 'POST', payload));
            await refreshAll();
            resetRoutingForm();
            setStatus('routingStatus', id ? 'Routing profile updated.' : 'Routing profile created.', 'ok');
          } catch (error) {
            setStatus('routingStatus', String(error.message || error), 'danger');
          }
        });

        byId('printerForm').addEventListener('submit', async function (event) {
          event.preventDefault();
          const form = event.currentTarget;
          const id = field(form, 'id').value.trim();
          const payload = {
            name: field(form, 'name').value.trim(),
            type: field(form, 'type').value,
            targetUri: field(form, 'targetUri').value.trim(),
            domainUsername: field(form, 'domainUsername').value.trim(),
            isActive: field(form, 'isActive').checked
          };
          const password = field(form, 'password').value;
          if (password) {
            payload.password = password;
          }

          try {
            await (id
              ? request('/admin/config/printers/' + id, 'PATCH', payload)
              : request('/admin/config/printers', 'POST', payload));
            await refreshAll();
            resetPrinterForm();
            setStatus('printerStatus', id ? 'Printer updated.' : 'Printer created.', 'ok');
          } catch (error) {
            setStatus('printerStatus', String(error.message || error), 'danger');
          }
        });

        byId('sourceForm').addEventListener('submit', async function (event) {
          event.preventDefault();
          const form = event.currentTarget;
          const id = field(form, 'id').value.trim();
          const payload = {
            path: field(form, 'path').value.trim(),
            domainUsername: field(form, 'domainUsername').value.trim(),
            printerDomainUsername: field(form, 'printerDomainUsername').value.trim(),
            routingProfileId: field(form, 'routingProfileId').value || null,
            a4PrinterId: field(form, 'a4PrinterId').value || null,
            thermalPrinterId: field(form, 'thermalPrinterId').value || null,
            includeFilenamePatterns: splitLines(field(form, 'includeFilenamePatterns').value),
            excludeFilenamePatterns: splitLines(field(form, 'excludeFilenamePatterns').value),
            isActive: field(form, 'isActive').checked
          };
          const password = field(form, 'password').value;
          const printerPassword = field(form, 'printerPassword').value;
          if (password) {
            payload.password = password;
          }
          if (printerPassword) {
            payload.printerPassword = printerPassword;
          }

          try {
            await (id
              ? request('/admin/config/smb-sources/' + id, 'PATCH', payload)
              : request('/admin/config/smb-sources', 'POST', payload));
            await refreshAll();
            resetSourceForm();
            setStatus('sourceStatus', id ? 'Mapping updated.' : 'Mapping created.', 'ok');
          } catch (error) {
            setStatus('sourceStatus', String(error.message || error), 'danger');
          }
        });

        byId('runPipelineButton').addEventListener('click', async function () {
          const button = byId('runPipelineButton');
          button.disabled = true;
          try {
            setStatus('runPipelineStatus', 'Running pipeline...', 'muted');
            const result = await request('/worker/pipeline/run-once', 'POST', {});
            const summary = result && result.summary;
            setStatus(
              'runPipelineStatus',
              summary
                ? 'Done: ' + summary.filesProcessed + ' file(s) processed, ' + summary.pageDispatches + ' page(s) printed, ' +
                  summary.filesSkippedDedup + ' duplicate(s) skipped, ' + summary.failures + ' failure(s).'
                : 'Pipeline run finished.',
              summary && summary.failures ? 'danger' : 'ok'
            );
            await refreshLiveStatus();
          } catch (error) {
            setStatus('runPipelineStatus', String(error.message || error), 'danger');
          } finally {
            button.disabled = false;
          }
        });

        byId('resetRoutingForm').addEventListener('click', resetRoutingForm);
        byId('resetPrinterForm').addEventListener('click', resetPrinterForm);
        byId('resetSourceForm').addEventListener('click', resetSourceForm);

        document.querySelectorAll('[data-tab]').forEach(function (button) {
          button.addEventListener('click', function () {
            showTab(button.getAttribute('data-tab'));
          });
        });

        document.body.addEventListener('click', async function (event) {
          const editPrinter = event.target.closest('[data-edit-printer]');
          if (editPrinter) {
            const printer = state.printers.find(function (entry) { return entry.id === editPrinter.getAttribute('data-edit-printer'); });
            if (printer) {
              fillPrinterForm(printer);
            }
            return;
          }

          const deletePrinter = event.target.closest('[data-delete-printer]');
          if (deletePrinter) {
            const printerToDelete = state.printers.find(function (entry) { return entry.id === deletePrinter.getAttribute('data-delete-printer'); });
            if (!window.confirm('Delete printer "' + (printerToDelete ? printerToDelete.name : 'unknown') + '"? Mappings that use it will stop routing to it.')) {
              return;
            }
            try {
              await request('/admin/config/printers/' + deletePrinter.getAttribute('data-delete-printer'), 'DELETE');
              await refreshAll();
              resetPrinterForm();
              setStatus('printerStatus', 'Printer deleted.', 'ok');
            } catch (error) {
              setStatus('printerStatus', String(error.message || error), 'danger');
            }
            return;
          }

          const editProfile = event.target.closest('[data-edit-profile]');
          if (editProfile) {
            const profile = state.routingProfiles.find(function (entry) { return entry.id === editProfile.getAttribute('data-edit-profile'); });
            if (profile) {
              fillRoutingForm(profile);
            }
            return;
          }

          const deleteProfile = event.target.closest('[data-delete-profile]');
          if (deleteProfile) {
            const profileToDelete = state.routingProfiles.find(function (entry) { return entry.id === deleteProfile.getAttribute('data-delete-profile'); });
            if (!window.confirm('Delete routing profile "' + (profileToDelete ? profileToDelete.name : 'unknown') + '"? Mappings that use it fall back to default routing.')) {
              return;
            }
            try {
              await request('/admin/config/routing-profiles/' + deleteProfile.getAttribute('data-delete-profile'), 'DELETE');
              await refreshAll();
              resetRoutingForm();
              setStatus('routingStatus', 'Recognition profile deleted.', 'ok');
            } catch (error) {
              setStatus('routingStatus', String(error.message || error), 'danger');
            }
            return;
          }

          const editSource = event.target.closest('[data-edit-source]');
          if (editSource) {
            const source = state.sources.find(function (entry) { return entry.id === editSource.getAttribute('data-edit-source'); });
            if (source) {
              fillSourceForm(source);
            }
            return;
          }

          const deleteSource = event.target.closest('[data-delete-source]');
          if (deleteSource) {
            const sourceToDelete = state.sources.find(function (entry) { return entry.id === deleteSource.getAttribute('data-delete-source'); });
            if (!window.confirm('Delete mapping for "' + (sourceToDelete ? sourceToDelete.path : 'unknown') + '"? Files in that folder will no longer be printed.')) {
              return;
            }
            try {
              await request('/admin/config/smb-sources/' + deleteSource.getAttribute('data-delete-source'), 'DELETE');
              await refreshAll();
              resetSourceForm();
              setStatus('sourceStatus', 'Mapping deleted.', 'ok');
            } catch (error) {
              setStatus('sourceStatus', String(error.message || error), 'danger');
            }
            return;
          }

          const retryJob = event.target.closest('[data-retry-job]');
          if (retryJob) {
            try {
              setTopStatus('Retrying job...', 'muted');
              await request('/worker/pipeline/jobs/' + retryJob.getAttribute('data-retry-job') + '/retry', 'POST');
              await refreshLiveStatus();
              setTopStatus('Job retry submitted.', 'ok');
            } catch (error) {
              setTopStatus(String(error.message || error), 'danger');
            }
            return;
          }

          const cancelJob = event.target.closest('[data-cancel-job]');
          if (cancelJob) {
            try {
              setTopStatus('Cancelling job...', 'muted');
              await request('/worker/pipeline/jobs/' + cancelJob.getAttribute('data-cancel-job') + '/cancel', 'POST');
              await refreshLiveStatus();
              setTopStatus('Job cancelled.', 'ok');
            } catch (error) {
              setTopStatus(String(error.message || error), 'danger');
            }
          }
        });

        setSession(null);
        showTab('status');
        resetRoutingForm();
        resetSourceForm();
        resetPrinterForm();
        renderStatusPanel();
        loadBootstrapState().catch(function () {});

        if (state.token) {
          setSession({ username: 'admin' });
          refreshAll().catch(function (error) {
            state.token = '';
            localStorage.removeItem('printo.admin.token');
            setSession(null);
            setTopStatus(String(error.message || error), 'danger');
          }).finally(function () {
            showTab('status');
          });
          startLivePolling();
        }
      })();
    </script>
  </body>
</html>`;
}

function registerProxyRoute(app: express.Application, options: { fetchImpl: FetchLike; apiBaseUrl: string }, definition: ProxyDefinition) {
  const method = definition.method;
  app[method](definition.path, async (req, res) => {
    const httpMethod = method.toUpperCase() as HttpMethod;
    return sendProxyRequest({
      req,
      res,
      fetchImpl: options.fetchImpl,
      apiBaseUrl: options.apiBaseUrl,
      upstreamPath: definition.upstreamPath(req),
      method: httpMethod
    });
  });
}

export function createWebApp(options: CreateWebAppOptions = {}) {
  const app = express();
  const fetchImpl = options.fetchImpl ?? fetch;
  const apiBaseUrl = options.apiBaseUrl ?? process.env.API_BASE_URL ?? 'http://127.0.0.1:4000';
  const workerBaseUrl = options.workerBaseUrl ?? process.env.WORKER_BASE_URL ?? 'http://127.0.0.1:5000';
  // Resolve pdfjs-dist wherever npm hoisted it — a fixed ../node_modules path
  // breaks under workspace hoisting and 404s the in-browser PDF previews.
  const require = createRequire(import.meta.url);
  const pdfjsDistDir = dirname(require.resolve('pdfjs-dist/package.json'));

  app.use(express.json({ limit: '30mb' }));
  app.use('/vendor/pdfjs', express.static(join(pdfjsDistDir, 'build')));

  app.get('/health', (_req, res) => {
    res.json({ service: 'web', status: 'ok' });
  });

  app.get('/favicon.ico', (_req, res) => {
    return res.status(204).end();
  });

  app.get('/', (_req, res) => {
    res.redirect('/admin/config');
  });

  app.get('/i18n/messages', (req, res) => {
    const locale = typeof req.query.locale === 'string' ? req.query.locale : undefined;
    const resolved = resolveMessages(locale);
    return res.json({
      locale: resolved.locale,
      defaultLocale: getDefaultLocale(),
      messages: resolved.messages
    });
  });

  for (const path of ['/auth/login', '/auth/bootstrap-admin']) {
    app.post(path, async (req, res) => {
      try {
        const headers: Record<string, string> = {
          accept: 'application/json',
          'content-type': 'application/json'
        };

        const bootstrapToken = req.header('x-bootstrap-token');
        if (bootstrapToken) {
          headers['x-bootstrap-token'] = bootstrapToken;
        }

        const response = await fetchImpl(`${apiBaseUrl}${path}`, {
          method: 'POST',
          headers,
          body: JSON.stringify(req.body ?? {})
        });

        const contentType = response.headers.get('content-type') ?? '';
        if (contentType.includes('application/json')) {
          const payload = await response.json();
          if (
            path === '/auth/login' &&
            response.ok &&
            (!payload?.user?.roles || !Array.isArray(payload.user.roles) || !payload.user.roles.includes('ADMIN'))
          ) {
            return res.status(403).json({ error: 'ADMIN_LOGIN_REQUIRED' });
          }
          return res.status(response.status).json(payload);
        }

        return res.status(response.status).send(await response.text());
      } catch {
        return res.status(502).json({ error: 'UPSTREAM_UNAVAILABLE' });
      }
    });
  }

  app.get('/auth/bootstrap-status', async (_req, res) => {
    try {
      const response = await fetchImpl(`${apiBaseUrl}/auth/bootstrap-status`, {
        method: 'GET',
        headers: { accept: 'application/json' }
      });
      return res.status(response.status).json(await response.json());
    } catch {
      return res.status(502).json({ error: 'UPSTREAM_UNAVAILABLE' });
    }
  });

  app.post('/admin/preview/routing-match', async (req, res) => {
    if (!resolveAuthHeader(req)) {
      return res.status(401).json({ error: 'MISSING_AUTH_TOKEN' });
    }

    const snippetBase64 = typeof req.body?.snippetBase64 === 'string' ? req.body.snippetBase64.trim() : '';
    const pdfBase64 = typeof req.body?.pdfBase64 === 'string' ? req.body.pdfBase64.trim() : '';
    const matchThreshold = Number(req.body?.matchThreshold ?? 0.88);

    if (!snippetBase64 || !pdfBase64 || !Number.isFinite(matchThreshold)) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }

    try {
      const result = await matchPdfPagesBySnippet({
        pdfBuffer: Buffer.from(pdfBase64, 'base64'),
        snippetBase64,
        matchThreshold
      });
      return res.json(result);
    } catch {
      return res.status(400).json({ error: 'PDF_MATCH_PREVIEW_FAILED' });
    }
  });

  app.get('/admin/config', (_req, res) => {
    res.type('html').send(renderAdminPage());
  });

  const apiProxyDefinitions: ProxyDefinition[] = [
    { method: 'get', path: '/me/preferences', upstreamPath: () => '/me/preferences' },
    { method: 'patch', path: '/me/preferences', upstreamPath: () => '/me/preferences' },
    { method: 'get', path: '/admin/config/printers', upstreamPath: () => '/admin/config/printers' },
    { method: 'post', path: '/admin/config/printers', upstreamPath: () => '/admin/config/printers' },
    { method: 'patch', path: '/admin/config/printers/:printerId', upstreamPath: (req) => `/admin/config/printers/${req.params.printerId}` },
    { method: 'delete', path: '/admin/config/printers/:printerId', upstreamPath: (req) => `/admin/config/printers/${req.params.printerId}` },
    { method: 'put', path: '/admin/config/user-printer-assignments/:userId', upstreamPath: (req) => `/admin/config/user-printer-assignments/${req.params.userId}` },
    { method: 'get', path: '/admin/config/routing-profiles', upstreamPath: () => '/admin/config/routing-profiles' },
    { method: 'post', path: '/admin/config/routing-profiles', upstreamPath: () => '/admin/config/routing-profiles' },
    {
      method: 'patch',
      path: '/admin/config/routing-profiles/:routingProfileId',
      upstreamPath: (req) => `/admin/config/routing-profiles/${req.params.routingProfileId}`
    },
    {
      method: 'delete',
      path: '/admin/config/routing-profiles/:routingProfileId',
      upstreamPath: (req) => `/admin/config/routing-profiles/${req.params.routingProfileId}`
    },
    { method: 'get', path: '/admin/config/smb-sources', upstreamPath: () => '/admin/config/smb-sources' },
    { method: 'post', path: '/admin/config/smb-sources', upstreamPath: () => '/admin/config/smb-sources' },
    { method: 'patch', path: '/admin/config/smb-sources/:sourceId', upstreamPath: (req) => `/admin/config/smb-sources/${req.params.sourceId}` },
    { method: 'delete', path: '/admin/config/smb-sources/:sourceId', upstreamPath: (req) => `/admin/config/smb-sources/${req.params.sourceId}` },
    { method: 'get', path: '/admin/config/system-settings', upstreamPath: () => '/admin/config/system-settings' },
    { method: 'put', path: '/admin/config/system-settings', upstreamPath: () => '/admin/config/system-settings' },
    { method: 'get', path: '/admin/logs', upstreamPath: () => '/admin/logs' }
  ];

  for (const definition of apiProxyDefinitions) {
    registerProxyRoute(app, { fetchImpl, apiBaseUrl }, definition);
  }

  const workerProxyDefinitions: ProxyDefinition[] = [
    { method: 'get', path: '/worker/pipeline/status', upstreamPath: () => '/pipeline/status' },
    { method: 'post', path: '/worker/pipeline/run-once', upstreamPath: () => '/pipeline/run-once' },
    { method: 'get', path: '/worker/pipeline/notifications', upstreamPath: () => '/pipeline/notifications' },
    { method: 'post', path: '/worker/pipeline/notifications/test', upstreamPath: () => '/pipeline/notifications/test' },
    { method: 'get', path: '/worker/pipeline/jobs', upstreamPath: () => '/pipeline/jobs' },
    { method: 'get', path: '/worker/pipeline/vision-status', upstreamPath: () => '/pipeline/vision-status' },
    { method: 'post', path: '/worker/pipeline/preview/classification', upstreamPath: () => '/pipeline/preview/classification' },
    { method: 'get', path: '/worker/pipeline/jobs/:jobId/pages', upstreamPath: (req) => `/pipeline/jobs/${req.params.jobId}/pages` },
    { method: 'post', path: '/worker/pipeline/jobs/:jobId/cancel', upstreamPath: (req) => `/pipeline/jobs/${req.params.jobId}/cancel` },
    { method: 'post', path: '/worker/pipeline/jobs/:jobId/retry', upstreamPath: (req) => `/pipeline/jobs/${req.params.jobId}/retry` }
  ];

  for (const definition of workerProxyDefinitions) {
    registerProxyRoute(app, { fetchImpl, apiBaseUrl: workerBaseUrl }, definition);
  }

  return app;
}
