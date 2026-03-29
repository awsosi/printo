import express, { type Request, type Response } from 'express';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
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

  const requestInit: RequestInit = {
    method,
    headers
  };

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
    <title>Printo Control Center</title>
    <style>
      :root {
        --bg: #f3f5f8;
        --bg-strong: #e7ecf3;
        --text: #0f1d2f;
        --muted: #4f6077;
        --line: #cfd9e6;
        --card: #ffffff;
        --brand: #1455c2;
        --brand-strong: #0f4299;
        --ok: #13723f;
        --danger: #b42318;
      }

      :root[data-theme='dark'] {
        --bg: #121821;
        --bg-strong: #1b2430;
        --text: #e8efff;
        --muted: #a3b1c6;
        --line: #31425a;
        --card: #1a2533;
        --brand: #5b9cff;
        --brand-strong: #3f81e8;
        --ok: #37c172;
        --danger: #ff6b6b;
      }

      * {
        box-sizing: border-box;
      }

      body {
        margin: 0;
        font-family: "Manrope", "Segoe UI", sans-serif;
        color: var(--text);
        background:
          radial-gradient(900px 360px at 5% -10%, #d5e8ff 0%, transparent 70%),
          radial-gradient(900px 460px at 100% 0%, #dff3e7 0%, transparent 72%),
          var(--bg);
      }

      :root[data-theme='dark'] body {
        background:
          radial-gradient(900px 360px at 5% -10%, rgba(20, 85, 194, 0.28) 0%, transparent 70%),
          radial-gradient(900px 460px at 100% 0%, rgba(19, 114, 63, 0.25) 0%, transparent 72%),
          var(--bg);
      }

      input,
      select,
      textarea,
      button {
        width: 100%;
        border-radius: 10px;
        border: 1px solid var(--line);
        padding: 9px 11px;
        font: inherit;
        color: inherit;
        background: var(--card);
      }

      textarea {
        min-height: 84px;
      }

      button {
        cursor: pointer;
        font-weight: 700;
        background: var(--brand);
        border-color: transparent;
        color: #fff;
      }

      button:hover {
        background: var(--brand-strong);
      }

      button.secondary {
        background: transparent;
        border-color: var(--line);
        color: var(--text);
      }

      button.ghost {
        background: transparent;
        border: 1px dashed var(--line);
        color: var(--text);
      }

      .shell {
        max-width: 1440px;
        margin: 0 auto;
        padding: 20px;
      }

      .header {
        display: grid;
        grid-template-columns: 1fr auto;
        gap: 12px;
        align-items: center;
        margin-bottom: 14px;
      }

      .header h1 {
        margin: 0;
        font-size: 1.8rem;
      }

      .header p {
        margin: 4px 0 0;
        color: var(--muted);
      }

      .session {
        display: inline-flex;
        gap: 8px;
        align-items: center;
        border: 1px solid var(--line);
        border-radius: 999px;
        padding: 8px 12px;
        background: var(--card);
        color: var(--muted);
      }

      .card {
        background: var(--card);
        border: 1px solid var(--line);
        border-radius: 14px;
        box-shadow: 0 8px 24px rgba(5, 15, 35, 0.08);
        padding: 14px;
      }

      .stack {
        display: grid;
        gap: 10px;
      }

      .row2 {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 8px;
      }

      .row3 {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: 8px;
      }

      .actions {
        display: flex;
        gap: 8px;
      }

      .actions button {
        flex: 1;
      }

      .muted {
        color: var(--muted);
        font-size: 0.92rem;
      }

      .ok {
        color: var(--ok);
      }

      .danger {
        color: var(--danger);
      }

      .auth-stage {
        display: grid;
        gap: 12px;
        max-width: 760px;
        margin: 0 auto;
      }

      .app-stage {
        display: none;
      }

      .app-layout {
        display: grid;
        grid-template-columns: 320px 1fr;
        gap: 14px;
      }

      .sidebar {
        display: grid;
        gap: 10px;
        align-content: start;
      }

      .step-nav {
        display: grid;
        gap: 6px;
      }

      .step-nav button {
        width: 100%;
        text-align: left;
        background: transparent;
        color: var(--text);
        border-color: var(--line);
      }

      .step-nav button.active {
        border-color: var(--brand);
        color: var(--brand);
        background: color-mix(in srgb, var(--brand) 10%, transparent);
      }

      .panel {
        display: none;
      }

      .panel.active {
        display: grid;
        gap: 12px;
      }

      .panel h2,
      .card h2,
      .card h3 {
        margin: 0 0 6px;
      }

      .panel h3 {
        margin: 0;
        font-size: 1rem;
      }

      table {
        width: 100%;
        border-collapse: collapse;
        border-radius: 10px;
        overflow: hidden;
        border: 1px solid var(--line);
        background: var(--card);
      }

      th,
      td {
        border-bottom: 1px solid var(--line);
        padding: 8px;
        text-align: left;
        vertical-align: top;
        font-size: 0.9rem;
      }

      th {
        background: color-mix(in srgb, var(--bg-strong) 65%, var(--card));
      }

      .inline {
        display: flex;
        gap: 6px;
      }

      .inline button {
        width: auto;
        padding: 6px 10px;
      }

      .split {
        display: grid;
        gap: 10px;
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }

      .dropzone {
        border: 1px dashed var(--line);
        border-radius: 10px;
        padding: 12px;
        text-align: center;
        background: color-mix(in srgb, var(--bg-strong) 52%, var(--card));
      }

      .dropzone.active {
        border-color: var(--brand);
      }

      .preview {
        width: 100%;
        max-height: 180px;
        object-fit: contain;
        background: #fff;
        border-radius: 10px;
        border: 1px solid var(--line);
      }

      .pdf-stage {
        position: relative;
        min-height: 280px;
        overflow: auto;
        border: 1px solid var(--line);
        border-radius: 10px;
        background: #fff;
        padding: 10px;
      }

      .pdf-stage canvas {
        display: block;
        width: 100%;
        height: auto;
      }

      .selection-box {
        position: absolute;
        border: 2px solid var(--brand);
        background: color-mix(in srgb, var(--brand) 14%, transparent);
        pointer-events: none;
        display: none;
      }

      .page-pills {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
      }

      .page-pills button {
        width: auto;
        padding: 6px 10px;
        background: transparent;
        color: var(--text);
        border-color: var(--line);
      }

      .page-pills button.active {
        border-color: var(--brand);
        color: var(--brand);
      }

      .page-pills button.match {
        background: color-mix(in srgb, var(--ok) 10%, transparent);
        border-color: var(--ok);
      }

      select[multiple] {
        min-height: 150px;
      }

      .badge {
        display: inline-block;
        border: 1px solid var(--line);
        border-radius: 999px;
        padding: 4px 8px;
        font-size: 0.78rem;
      }

      .kpi {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: 8px;
      }

      .kpi .card {
        padding: 10px;
      }

      .kpi strong {
        display: block;
        font-size: 1.3rem;
      }

      @media (max-width: 1140px) {
        .app-layout {
          grid-template-columns: 1fr;
        }
      }

      @media (max-width: 760px) {
        .shell {
          padding: 12px;
        }

        .header {
          grid-template-columns: 1fr;
        }

        .row2,
        .row3,
        .split,
        .kpi {
          grid-template-columns: 1fr;
        }
      }
    </style>
  </head>
  <body>
    <div class="shell">
      <header class="header">
        <div>
          <h1 data-i18n="nav.adminConfig">Admin configuration</h1>
          <p>Guided setup flow: admin access, users/groups, AD sync, visual profile, printers, SMB, routing, assignments.</p>
        </div>
        <div id="authSessionBadge" class="session">Signed out</div>
      </header>

      <section id="authStage" class="auth-stage">
        <section id="bootstrapPanel" class="card" style="display:none;">
          <h2>Step 1: Bootstrap admin account</h2>
          <p class="muted">No users exist yet. Create the first administrator account before any configuration.</p>
          <form id="bootstrapForm" class="stack">
            <div class="row2">
              <input id="bootstrapUsername" name="username" placeholder="admin username" autocomplete="username" required />
              <input id="bootstrapPassword" name="password" type="password" placeholder="admin password" autocomplete="new-password" required />
            </div>
            <input id="bootstrapToken" name="token" placeholder="bootstrap token (optional)" />
            <button type="submit">Create initial admin</button>
          </form>
          <div id="bootstrapStatus" class="muted"></div>
        </section>

        <section class="card">
          <h2>Step 1: Admin login</h2>
          <p class="muted">Configuration panels are hidden until an admin signs in.</p>
          <form id="loginForm" class="stack">
            <div class="row2">
              <input id="loginUsername" name="username" placeholder="username" autocomplete="username" required />
              <input id="loginPassword" name="password" type="password" placeholder="password" autocomplete="current-password" required />
            </div>
            <div class="actions">
              <button type="submit">Sign in</button>
              <button id="logoutButton" type="button" class="secondary">Sign out</button>
            </div>
          </form>
          <div id="authStatus" class="muted"></div>
        </section>
      </section>

      <section id="appStage" class="app-stage">
        <div class="app-layout">
          <aside class="sidebar">
            <section class="card stack">
              <h2>Preferences</h2>
              <select id="localeSelect">
                <option value="en-US">English (en-US)</option>
                <option value="pl-PL">Polski (pl-PL)</option>
              </select>
              <select id="themeSelect">
                <option value="system">System</option>
                <option value="light">Light</option>
                <option value="dark">Dark</option>
              </select>
              <button id="savePreferences" type="button">Save preferences</button>
              <div id="prefStatus" class="muted"></div>
            </section>

            <section class="card">
              <h2>Setup flow</h2>
              <div id="stepNav" class="step-nav"></div>
            </section>

            <section class="card">
              <h2>Global status</h2>
              <div id="globalStatus" class="muted">Ready</div>
            </section>
          </aside>

          <main class="stack">
            <section class="panel active" data-panel="admin-accounts">
              <div class="kpi">
                <div class="card"><span class="muted">Admins</span><strong id="kpiAdmins">0</strong></div>
                <div class="card"><span class="muted">Managed users</span><strong id="kpiUsers">0</strong></div>
                <div class="card"><span class="muted">Groups</span><strong id="kpiGroups">0</strong></div>
              </div>

              <section class="card stack">
                <h2>Admin accounts</h2>
                <p class="muted">Separate from managed users and groups.</p>
                <form id="adminCreateForm" class="row3">
                  <input name="username" placeholder="admin username" autocomplete="username" required />
                  <input name="password" type="password" placeholder="admin password" autocomplete="new-password" required />
                  <button type="submit">Create admin</button>
                </form>
                <table id="adminsTableWrap">
                  <thead><tr><th>ID</th><th>Username</th><th>Role</th><th>Actions</th></tr></thead>
                  <tbody id="adminsTable"></tbody>
                </table>
              </section>

              <section class="card stack">
                <h2>Step 2: Users & Groups</h2>
                <p class="muted">These users are managed identities for assignment. They do not need to sign in to this app.</p>
                <div class="split">
                  <form id="userCreateForm" class="stack">
                    <h3>Create managed user</h3>
                    <input name="username" placeholder="user identifier" autocomplete="username" required />
                    <input name="password" type="password" autocomplete="new-password" placeholder="generated secret (for API constraints)" />
                    <label><input name="isRemoteEnabled" type="checkbox" /> Remote auth enabled</label>
                    <button type="submit">Create user</button>
                  </form>

                  <form id="groupCreateForm" class="stack">
                    <h3>Create group</h3>
                    <input name="name" placeholder="group name" required />
                    <input name="description" placeholder="description" />
                    <button type="submit">Create group</button>
                  </form>
                </div>

                <table id="usersTableWrap">
                  <thead><tr><th>ID</th><th>User</th><th>Remote</th><th>Actions</th></tr></thead>
                  <tbody id="usersTable"></tbody>
                </table>

                <table id="groupsTableWrap">
                  <thead><tr><th>ID</th><th>Group</th><th>Description</th><th>Actions</th></tr></thead>
                  <tbody id="groupsTable"></tbody>
                </table>

                <form id="groupMembershipForm" class="row3">
                  <select name="groupId" id="membershipGroupSelect" required></select>
                  <select name="userId" id="membershipUserSelect" required></select>
                  <button type="submit">Add membership</button>
                </form>
                <table id="membershipsTableWrap">
                  <thead><tr><th>Group</th><th>User</th><th>Actions</th></tr></thead>
                  <tbody id="membershipsTable"></tbody>
                </table>
              </section>
            </section>

            <section class="panel" data-panel="ad-sync">
              <section class="card stack">
                <h2>Step 3: Active Directory sync (read-only)</h2>
                <p class="muted">Configure AD service account, discover users/groups/shares/printers, and import selected objects.</p>
                <form id="adConfigForm" class="row3">
                  <label><input name="enabled" type="checkbox" /> Enable AD sync</label>
                  <input name="serverUrl" id="adServerUrl" placeholder="AD sync bridge URL (optional if AD_SYNC_API_BASE_URL env is set)" />
                  <input name="domain" id="adDomain" placeholder="domain (e.g. CORP.LOCAL)" />
                  <input name="baseDn" id="adBaseDn" placeholder="base DN (e.g. DC=corp,DC=local)" />
                  <input name="bindUsername" id="adBindUsername" placeholder="service account username" />
                  <input name="bindSecretRef" id="adBindSecretRef" placeholder="service account secret ref (e.g. env:AD_BIND_PASS)" />
                  <button type="submit">Save AD sync config</button>
                </form>

                <div class="row3">
                  <input id="adBindPassword" type="password" form="adConfigForm" autocomplete="off" placeholder="optional runtime bind password (not stored)" />
                  <input id="adDefaultSmbDomainUsername" placeholder="default SMB username for imported shares" />
                  <input id="adDefaultSmbSecretRef" placeholder="default SMB secret ref for imported shares" />
                </div>

                <div class="actions">
                  <button id="adDiscoverButton" type="button">Discover AD objects</button>
                  <button id="adImportButton" type="button" class="secondary">Import selected objects</button>
                </div>

                <div class="split">
                  <div class="stack">
                    <h3>AD users</h3>
                    <select id="adUsersSelect" multiple></select>
                  </div>
                  <div class="stack">
                    <h3>AD groups</h3>
                    <select id="adGroupsSelect" multiple></select>
                  </div>
                </div>
                <div class="split">
                  <div class="stack">
                    <h3>AD SMB shares</h3>
                    <select id="adSmbSharesSelect" multiple></select>
                  </div>
                  <div class="stack">
                    <h3>AD printers</h3>
                    <select id="adPrintersSelect" multiple></select>
                  </div>
                </div>
                <div id="adSyncStatus" class="muted"></div>
              </section>
            </section>

            <section class="panel" data-panel="visual-wizard">
              <section class="card stack">
                <h2>Step 4: Visual profile wizard</h2>
                <p class="muted">Paste, drag-and-drop, or open an image file. Easy replace keeps preview and updates snippet.</p>
                <form id="visualProfileForm" class="stack">
                  <div class="row3">
                    <input name="name" placeholder="profile name" required />
                    <select name="matchMode"><option value="CONTAINS">CONTAINS</option><option value="EXACT">EXACT</option></select>
                    <input name="labels" placeholder="labels comma separated" />
                    <select name="ownerUserId" id="visualOwnerUserSelect"></select>
                    <select name="ownerGroupId" id="visualOwnerGroupSelect"></select>
                    <select name="routeType"><option value="">No forced type</option><option value="A4">A4</option><option value="THERMAL">THERMAL</option></select>
                    <select name="printerId" id="visualPrinterSelect"></select>
                  </div>

                  <div class="split">
                    <div class="stack">
                      <textarea id="visualSnippet" name="snippetBase64" placeholder="base64 image snippet" required></textarea>
                      <input id="visualFileInput" type="file" accept="image/*" style="display:none;" />
                      <div class="actions">
                        <button id="visualChooseFile" type="button" class="secondary">Open image file</button>
                        <button id="visualReplace" type="button" class="ghost">Replace snippet</button>
                      </div>
                    </div>
                    <div class="stack">
                      <div id="visualDropzone" class="dropzone">Drop image here or paste image from clipboard</div>
                      <img id="visualPreview" class="preview" alt="visual preview" />
                    </div>
                  </div>
                  <button type="submit">Create visual profile</button>
                </form>
                <table>
                  <thead><tr><th>ID</th><th>Name</th><th>Mode</th><th>Route</th><th>Owner</th><th>Actions</th></tr></thead>
                  <tbody id="visualProfilesTable"></tbody>
                </table>
              </section>
            </section>

            <section class="panel" data-panel="printers">
              <section class="card stack">
                <h2>Step 5: Printers</h2>
                <div class="split">
                  <div class="stack">
                    <h3>Global printer credentials</h3>
                    <div class="muted">Managed in Operations → System settings and applied when a printer override is left blank.</div>
                  </div>
                  <div class="muted">
                    Each printer can override username and password/secret.
                  </div>
                </div>

                <form id="printerCreateForm" class="row3">
                  <input name="name" placeholder="printer name" required />
                  <select name="type"><option value="A4">A4</option><option value="THERMAL">THERMAL</option></select>
                  <input name="targetUri" placeholder="target URI" required />
                  <input name="domainUsername" placeholder="domain username (blank = global)" />
                  <input name="password" type="password" autocomplete="off" placeholder="password override (optional)" />
                  <input name="secretRef" placeholder="secret ref override (optional)" />
                  <button type="submit">Create printer</button>
                </form>

                <table>
                  <thead><tr><th>ID</th><th>Name</th><th>Type</th><th>Target</th><th>Credentials</th><th>Actions</th></tr></thead>
                  <tbody id="printersTable"></tbody>
                </table>
              </section>
            </section>

            <section class="panel" data-panel="smb-sources">
              <section class="card stack">
                <h2>Step 6: SMB sources</h2>
                <div class="split">
                  <div class="stack">
                    <h3>Global SMB credentials</h3>
                    <div class="muted">Managed in Operations → System settings and applied when a source override is left blank.</div>
                  </div>
                  <div class="muted">Each SMB source may override username and secret ref.</div>
                </div>

                <form id="smbCreateForm" class="row3">
                  <input name="path" placeholder="SMB path" required />
                  <input name="domainUsername" placeholder="domain username (blank = global)" />
                  <input name="password" type="password" autocomplete="off" placeholder="password override (optional)" />
                  <input name="secretRef" placeholder="secret ref (blank = global)" />
                  <select name="ownerUserId" id="smbOwnerUserSelect"></select>
                  <select name="ownerGroupId" id="smbOwnerGroupSelect"></select>
                  <button type="submit">Create SMB source</button>
                </form>

                <table>
                  <thead><tr><th>ID</th><th>Path</th><th>Credentials</th><th>Owner</th><th>Actions</th></tr></thead>
                  <tbody id="smbTable"></tbody>
                </table>
              </section>

              <section class="card stack">
                <h2>Filename masks</h2>
                <p class="muted">Masks are managed with SMB source configuration, not routing.</p>
                <form id="maskCreateForm" class="row3">
                  <input name="pattern" placeholder="filename pattern" required />
                  <label><input name="isRegex" type="checkbox" /> regex</label>
                  <select name="ownerUserId" id="maskOwnerUserSelect"></select>
                  <select name="ownerGroupId" id="maskOwnerGroupSelect"></select>
                  <button type="submit">Create mask</button>
                </form>
                <table>
                  <thead><tr><th>ID</th><th>Pattern</th><th>Regex</th><th>Owner</th><th>Actions</th></tr></thead>
                  <tbody id="masksTable"></tbody>
                </table>
              </section>
            </section>

            <section class="panel" data-panel="routing">
              <section class="card stack">
                <h2>Step 7: Routing</h2>
                <p class="muted">Load a sample PDF, draw the region that identifies thermal pages, and flip selection when the inverse pages should be routed.</p>
                <form id="routingCreateForm" class="stack">
                  <input type="hidden" name="routingProfileId" />
                  <input type="hidden" name="samplePdfName" />
                  <input type="hidden" name="samplePdfBase64" />
                  <input type="hidden" name="visualRulesJson" />
                  <div class="row3">
                    <input name="name" placeholder="routing profile name" required />
                    <select name="defaultRouteType">
                      <option value="A4">Default A4, selection routes THERMAL</option>
                      <option value="THERMAL">Default THERMAL, selection routes A4</option>
                    </select>
                    <select name="fallbackPrinterId" id="routingFallbackPrinterSelect"></select>
                    <select name="ownerUserId" id="routingOwnerUserSelect"></select>
                    <select name="ownerGroupId" id="routingOwnerGroupSelect"></select>
                    <input name="patterns" placeholder="legacy thermal text patterns comma separated" />
                  </div>

                  <div class="split">
                    <div class="stack">
                      <input id="routingSampleFile" type="file" accept="application/pdf" />
                      <div class="actions">
                        <button id="routingLoadSample" type="button" class="secondary">Load sample PDF</button>
                        <button id="routingFlipSelection" type="button" class="ghost">Flip selection</button>
                        <button id="routingClearRule" type="button" class="ghost">Clear visual rule</button>
                      </div>
                      <div id="routingSelectionSummary" class="muted">No visual rule yet. Load a PDF and drag a rectangle over the page marker region.</div>
                      <div id="routingMatchedPages" class="muted"></div>
                    </div>
                    <div class="stack">
                      <div id="routingPdfStage" class="pdf-stage">
                        <canvas id="routingPdfCanvas"></canvas>
                        <div id="routingSelectionBox" class="selection-box"></div>
                      </div>
                      <div id="routingPageButtons" class="page-pills"></div>
                    </div>
                  </div>
                  <button type="submit">Save routing profile</button>
                </form>

                <table>
                  <thead><tr><th>ID</th><th>Name</th><th>Visual rule</th><th>Fallback</th><th>Owner</th><th>Actions</th></tr></thead>
                  <tbody id="routingTable"></tbody>
                </table>
              </section>
            </section>

            <section class="panel" data-panel="assignments">
              <section class="card stack">
                <h2>Step 8: Assign SMB, printers, routing to users/groups</h2>
                <div class="split">
                  <form id="userPrinterAssignmentForm" class="stack">
                    <h3>User printer assignment</h3>
                    <select name="userId" id="assignUserSelect" required></select>
                    <select name="a4PrinterId" id="assignA4Select"></select>
                    <select name="thermalPrinterId" id="assignThermalSelect"></select>
                    <button type="submit">Save user assignment</button>
                  </form>

                  <form id="groupPrinterApplyForm" class="stack">
                    <h3>Apply printer defaults to all users in group</h3>
                    <select name="groupId" id="groupApplySelect" required></select>
                    <select name="a4PrinterId" id="groupApplyA4Select"></select>
                    <select name="thermalPrinterId" id="groupApplyThermalSelect"></select>
                    <button type="submit">Apply now</button>
                  </form>
                </div>

                <div class="split">
                  <form id="smbAssignForm" class="stack">
                    <h3>SMB source assignment</h3>
                    <select name="sourceId" id="assignSmbSourceSelect" required></select>
                    <select name="ownerUserId" id="assignSmbUserSelect"></select>
                    <select name="ownerGroupId" id="assignSmbGroupSelect"></select>
                    <button type="submit">Assign SMB source</button>
                  </form>

                  <form id="routingAssignForm" class="stack">
                    <h3>Routing assignment</h3>
                    <select name="routingProfileId" id="assignRoutingSelect" required></select>
                    <select name="ownerUserId" id="assignRoutingUserSelect"></select>
                    <select name="ownerGroupId" id="assignRoutingGroupSelect"></select>
                    <button type="submit">Assign routing profile</button>
                  </form>
                </div>

                <table>
                  <thead><tr><th>User</th><th>A4</th><th>THERMAL</th><th>Actions</th></tr></thead>
                  <tbody id="assignmentsTable"></tbody>
                </table>
              </section>
            </section>

            <section class="panel" data-panel="ocr">
              <section class="card stack">
                <h2>OCR configuration</h2>
                <form id="ocrGlobalForm" class="stack">
                  <input name="provider" value="tesseract" placeholder="provider" required />
                  <textarea name="config">{"language":"eng"}</textarea>
                  <button type="submit">Save OCR global config</button>
                </form>
                <pre id="ocrGlobalPreview" class="muted"></pre>

                <form id="ocrOverrideForm" class="row3">
                  <select name="userId" id="ocrOverrideUserSelect" required></select>
                  <input name="provider" placeholder="provider override" />
                  <textarea name="config">{}</textarea>
                  <button type="submit">Save override</button>
                </form>
                <table>
                  <thead><tr><th>User</th><th>Provider</th><th>Config</th><th>Actions</th></tr></thead>
                  <tbody id="ocrOverridesTable"></tbody>
                </table>
              </section>
            </section>

            <section class="panel" data-panel="operations">
              <section class="card stack">
                <h2>Operations</h2>
                <p class="muted">Manual worker trigger, retry-safe job history, and recent admin audit events.</p>
                <form id="systemSettingsForm" class="stack">
                  <h3>System settings, defaults, and SMTP</h3>
                  <div class="row3">
                    <input name="globalSmbDomainUsername" id="globalSmbUsername" placeholder="default SMB domain username" />
                    <input name="globalSmbSecretRef" id="globalSmbSecretRef" placeholder="default SMB secret ref" />
                    <input name="workerPollIntervalMs" id="workerPollIntervalMs" type="number" min="1000" step="1000" placeholder="worker poll interval ms" />
                    <input name="globalPrinterDomainUsername" id="globalPrinterUsername" placeholder="default printer domain username" />
                    <input name="globalPrinterSecretRef" id="globalPrinterSecretRef" placeholder="default printer secret ref" />
                    <div class="muted">Blank per-source or per-printer credentials inherit these defaults.</div>
                  </div>
                  <div class="row3">
                    <label><input name="smtpEnabled" id="smtpEnabled" type="checkbox" /> Enable SMTP notifications</label>
                    <input name="smtpHost" id="smtpHost" placeholder="SMTP host" />
                    <input name="smtpPort" id="smtpPort" type="number" min="1" max="65535" placeholder="SMTP port" />
                    <label><input name="smtpSecure" id="smtpSecure" type="checkbox" /> Secure SMTP</label>
                    <input name="smtpUsername" id="smtpUsername" placeholder="SMTP username" />
                    <input name="smtpSecretRef" id="smtpSecretRef" placeholder="SMTP secret ref" />
                    <input name="smtpFrom" id="smtpFrom" placeholder="SMTP from address" />
                    <input name="smtpTo" id="smtpTo" placeholder="SMTP recipients comma separated" />
                    <button type="submit">Save system settings</button>
                  </div>
                </form>
                <div class="actions">
                  <button id="runPipelineNow" type="button">Run worker now</button>
                  <button id="sendTestNotification" type="button" class="secondary">Send SMTP test</button>
                  <button id="refreshOperations" type="button" class="secondary">Refresh operations</button>
                </div>
                <pre id="workerStatusPreview" class="muted"></pre>
                <table>
                  <thead><tr><th>Job</th><th>File</th><th>Status</th><th>Pages</th><th>Actions</th></tr></thead>
                  <tbody id="operationsJobsTable"></tbody>
                </table>
                <table>
                  <thead><tr><th>Time</th><th>Type</th><th>Subject</th><th>Status</th><th>Details</th></tr></thead>
                  <tbody id="notificationAttemptsTable"></tbody>
                </table>
                <table>
                  <thead><tr><th>Time</th><th>Action</th><th>Status</th><th>Actor</th><th>Meta</th></tr></thead>
                  <tbody id="auditLogsTable"></tbody>
                </table>
              </section>
            </section>
          </main>
        </div>
      </section>
    </div>

    <script src="/vendor/pdfjs/pdf.min.js"></script>
    <script>
      (function () {
        const STORAGE_KEYS = {
          token: 'printo_auth_token',
          user: 'printo_auth_user',
          locale: 'printo_locale',
          theme: 'printo_theme'
        };

        const state = {
          authToken: localStorage.getItem(STORAGE_KEYS.token) || '',
          authUser: localStorage.getItem(STORAGE_KEYS.user) || '',
          locale: localStorage.getItem(STORAGE_KEYS.locale) || 'en-US',
          theme: localStorage.getItem(STORAGE_KEYS.theme) || 'system',
          messages: {},
          requiresBootstrap: false,
          users: [],
          groups: [],
          memberships: [],
          smbSources: [],
          printers: [],
          routingProfiles: [],
          systemSettings: null,
          pipelineJobs: [],
          pipelineJobPages: {},
          notificationAttempts: [],
          auditLogs: [],
          routingEditor: {
            pdfDoc: null,
            pages: [],
            currentPageNumber: 1,
            samplePdfName: '',
            samplePdfBase64: '',
            visualRule: null,
            matchingPageNumbers: [],
            dragStart: null
          },
          adDiscovery: {
            users: [],
            groups: [],
            smbShares: [],
            printers: []
          }
        };

        if (window.pdfjsLib && window.pdfjsLib.GlobalWorkerOptions) {
          window.pdfjsLib.GlobalWorkerOptions.workerSrc = '/vendor/pdfjs/pdf.worker.min.js';
        }

        const navSteps = [
          ['admin-accounts', 'setup.step.1_2', '1-2. Admin access, users & groups'],
          ['ad-sync', 'setup.step.3', '3. Active Directory sync'],
          ['visual-wizard', 'setup.step.4', '4. Visual profile wizard'],
          ['printers', 'setup.step.5', '5. Printers'],
          ['smb-sources', 'setup.step.6', '6. SMB sources + masks'],
          ['routing', 'setup.step.7', '7. Routing'],
          ['assignments', 'setup.step.8', '8. Assignments'],
          ['ocr', 'setup.step.ocr', 'OCR'],
          ['operations', 'setup.step.operations', 'Operations']
        ];

        function t(key, fallback) {
          return state.messages[key] || fallback;
        }

        function status(text, klass) {
          const el = document.getElementById('globalStatus');
          if (!el) return;
          el.textContent = text || '';
          el.className = klass ? klass : 'muted';
        }

        function setSession(token, user) {
          state.authToken = token || '';
          state.authUser = user || '';
          localStorage.setItem(STORAGE_KEYS.token, state.authToken);
          localStorage.setItem(STORAGE_KEYS.user, state.authUser);
          document.getElementById('authSessionBadge').textContent = state.authToken
            ? t('auth.signedInAs', 'Signed in as') + ' ' + (state.authUser || 'admin')
            : t('auth.signedOut', 'Signed out');
          const authStage = document.getElementById('authStage');
          const appStage = document.getElementById('appStage');
          authStage.style.display = state.authToken ? 'none' : 'grid';
          appStage.style.display = state.authToken ? 'block' : 'none';
        }

        function tokenHeaders(withJson) {
          const headers = {};
          if (state.authToken) {
            headers['x-auth-token'] = state.authToken;
          }
          if (withJson) {
            headers['content-type'] = 'application/json';
          }
          return headers;
        }

        async function api(path, method, body) {
          const res = await fetch(path, {
            method: method || 'GET',
            headers: tokenHeaders(Boolean(body)),
            body: body ? JSON.stringify(body) : undefined
          });
          if (res.status === 401) {
            if (state.authToken) {
              setSession('', '');
              status('Session expired. Sign in again.', 'muted');
            }
            throw new Error('UNAUTHORIZED');
          }
          if (res.status === 204) return null;
          const ct = res.headers.get('content-type') || '';
          const payload = ct.includes('application/json') ? await res.json() : await res.text();
          if (!res.ok) {
            throw new Error(typeof payload === 'string' ? payload : payload.error || 'REQUEST_FAILED');
          }
          return payload;
        }

        function splitCsv(value) {
          return String(value || '').split(',').map((v) => v.trim()).filter(Boolean);
        }

        function optional(value) {
          const clean = String(value || '').trim();
          return clean ? clean : null;
        }

        function isValidDomainUsername(value) {
          const input = String(value || '').trim();
          if (!input) {
            return false;
          }

          for (const ch of input) {
            if (ch.trim() === '' || ch === '/') {
              return false;
            }
          }

          const backslash = String.fromCharCode(92);
          if (input.includes(backslash)) {
            if (input.includes('@')) {
              return false;
            }
            const parts = input.split(backslash);
            return parts.length === 2 && Boolean(parts[0]) && Boolean(parts[1]);
          }

          if (input.includes('@')) {
            const parts = input.split('@');
            if (parts.length !== 2 || !parts[0] || !parts[1]) {
              return false;
            }
            return parts[1].includes('.') && !parts[1].startsWith('.') && !parts[1].endsWith('.');
          }

          return true;
        }

        function noneOption(label) {
          return '<option value="">' + label + '</option>';
        }

        function ensurePassword(input) {
          const explicit = String(input || '').trim();
          if (explicit) return explicit;
          return 'managed-' + Math.random().toString(36).slice(2, 12);
        }

        function buildActionButtons(actions) {
          const wrap = document.createElement('div');
          wrap.className = 'inline';
          actions.forEach((entry) => {
            const button = document.createElement('button');
            button.type = 'button';
            button.textContent = entry.label;
            button.className = entry.secondary ? 'secondary' : '';
            button.addEventListener('click', entry.onClick);
            wrap.appendChild(button);
          });
          return wrap;
        }

        function fillTable(id, rows, render, emptyCols) {
          const body = document.getElementById(id);
          body.innerHTML = '';
          if (!rows || rows.length === 0) {
            const tr = document.createElement('tr');
            tr.innerHTML = '<td colspan="' + String(emptyCols || 6) + '" class="muted">' + t('table.noRecords', 'No records') + '</td>';
            body.appendChild(tr);
            return;
          }
          rows.forEach((row) => body.appendChild(render(row)));
        }

        function setOptions(selectId, options, placeholder) {
          const el = document.getElementById(selectId);
          if (!el) return;
          const previous = el.value;
          el.innerHTML = noneOption(placeholder || t('select.none', 'None')) + options.map((entry) => {
            return '<option value="' + entry.value + '">' + entry.label + '</option>';
          }).join('');
          if (previous && options.some((entry) => entry.value === previous)) {
            el.value = previous;
          }
        }

        function refreshSelectors() {
          const userOptions = state.users.map((u) => ({ value: u.id, label: u.username + ' (' + u.id.slice(0, 8) + ')' }));
          const groupOptions = state.groups.map((g) => ({ value: g.id, label: g.name + ' (' + g.id.slice(0, 8) + ')' }));
          const printerOptions = state.printers.map((p) => ({ value: p.id, label: p.name + ' [' + p.type + ']' }));
          const smbOptions = state.smbSources.map((s) => ({ value: s.id, label: s.path + ' (' + s.id.slice(0, 8) + ')' }));
          const routingOptions = state.routingProfiles.map((r) => ({ value: r.id, label: r.name + ' (' + r.id.slice(0, 8) + ')' }));

          [
            'membershipUserSelect', 'assignUserSelect', 'ocrOverrideUserSelect',
            'visualOwnerUserSelect', 'smbOwnerUserSelect', 'maskOwnerUserSelect',
            'routingOwnerUserSelect', 'assignSmbUserSelect', 'assignRoutingUserSelect'
          ].forEach((id) => setOptions(id, userOptions, t('select.noUser', 'No user')));

          [
            'membershipGroupSelect', 'visualOwnerGroupSelect', 'smbOwnerGroupSelect',
            'maskOwnerGroupSelect', 'routingOwnerGroupSelect', 'groupApplySelect',
            'assignSmbGroupSelect', 'assignRoutingGroupSelect'
          ].forEach((id) => setOptions(id, groupOptions, t('select.noGroup', 'No group')));

          [
            'visualPrinterSelect', 'routingFallbackPrinterSelect', 'assignA4Select',
            'assignThermalSelect', 'groupApplyA4Select', 'groupApplyThermalSelect'
          ].forEach((id) => setOptions(id, printerOptions, t('select.noPrinter', 'No printer')));

          setOptions('assignSmbSourceSelect', smbOptions, t('select.noSmbSource', 'No SMB source'));
          setOptions('assignRoutingSelect', routingOptions, t('select.noRoutingProfile', 'No routing profile'));
        }

        function setMultiSelectOptions(selectId, options) {
          const select = document.getElementById(selectId);
          if (!select) return;
          select.innerHTML = options
            .map((entry) => '<option value="' + entry.value + '">' + entry.label + '</option>')
            .join('');
        }

        function selectedValues(selectId) {
          const select = document.getElementById(selectId);
          if (!select) return [];
          return Array.from(select.selectedOptions).map((option) => option.value);
        }

        function normalizeRouteText(value) {
          return String(value || '').replace(/\s+/g, ' ').trim().toLowerCase();
        }

        function arrayBufferToBase64(buffer) {
          let binary = '';
          const bytes = new Uint8Array(buffer);
          for (let index = 0; index < bytes.length; index += 1) {
            binary += String.fromCharCode(bytes[index]);
          }
          return btoa(binary);
        }

        function base64ToUint8Array(base64) {
          const binary = atob(String(base64 || ''));
          const bytes = new Uint8Array(binary.length);
          for (let index = 0; index < binary.length; index += 1) {
            bytes[index] = binary.charCodeAt(index);
          }
          return bytes;
        }

        function readFileAsArrayBuffer(file) {
          return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => resolve(reader.result);
            reader.onerror = () => reject(reader.error || new Error('FILE_READ_FAILED'));
            reader.readAsArrayBuffer(file);
          });
        }

        function getRoutingForm() {
          return document.getElementById('routingCreateForm');
        }

        function getRoutingDefaultRouteType() {
          const form = getRoutingForm();
          return String(new FormData(form).get('defaultRouteType') || 'A4');
        }

        function getRoutingSelectedRouteType() {
          return getRoutingDefaultRouteType() === 'A4' ? 'THERMAL' : 'A4';
        }

        function getRoutingVisualRulePayload() {
          if (!state.routingEditor.visualRule) {
            return [];
          }
          return [
            {
              id: state.routingEditor.visualRule.id || '',
              samplePageNumber: state.routingEditor.visualRule.samplePageNumber,
              routeType: getRoutingSelectedRouteType(),
              matchMode: state.routingEditor.visualRule.matchMode || 'CONTAINS',
              expectedText: state.routingEditor.visualRule.expectedText || '',
              expectedWords: state.routingEditor.visualRule.expectedWords || [],
              rect: state.routingEditor.visualRule.rect
            }
          ];
        }

        function extractPageTextByRect(pageData, rect) {
          const left = rect.x * pageData.width;
          const top = rect.y * pageData.height;
          const right = left + rect.width * pageData.width;
          const bottom = top + rect.height * pageData.height;
          return normalizeRouteText(
            (pageData.textItems || [])
              .filter((item) => {
                const itemRight = item.x + item.width;
                const itemBottom = item.y + item.height;
                return itemRight >= left && item.x <= right && itemBottom >= top && item.y <= bottom;
              })
              .map((item) => item.text)
              .join(' ')
          );
        }

        function evaluateRoutingMatches(rule) {
          return state.routingEditor.pages
            .filter((page) => {
              const regionText = extractPageTextByRect(page, rule.rect);
              if (!regionText) return false;
              const expectedText = normalizeRouteText(rule.expectedText);
              if (rule.matchMode === 'EXACT' && expectedText) {
                return regionText === expectedText;
              }
              if (expectedText && regionText.includes(expectedText)) {
                return true;
              }
              return (rule.expectedWords || []).every((word) => regionText.includes(normalizeRouteText(word)));
            })
            .map((page) => page.pageNumber);
        }

        function renderRoutingSelectionOverlay() {
          const box = document.getElementById('routingSelectionBox');
          const canvas = document.getElementById('routingPdfCanvas');
          const rule = state.routingEditor.visualRule;
          if (!rule || !canvas.width || !canvas.height) {
            box.style.display = 'none';
            return;
          }
          const currentPage = state.routingEditor.pages.find((page) => page.pageNumber === state.routingEditor.currentPageNumber);
          if (!currentPage || currentPage.pageNumber !== rule.samplePageNumber) {
            box.style.display = 'none';
            return;
          }
          box.style.display = 'block';
          box.style.left = (canvas.offsetLeft + rule.rect.x * canvas.clientWidth) + 'px';
          box.style.top = (canvas.offsetTop + rule.rect.y * canvas.clientHeight) + 'px';
          box.style.width = (rule.rect.width * canvas.clientWidth) + 'px';
          box.style.height = (rule.rect.height * canvas.clientHeight) + 'px';
        }

        function updateRoutingSummary() {
          const summary = document.getElementById('routingSelectionSummary');
          const matched = document.getElementById('routingMatchedPages');
          const hiddenRules = document.querySelector('#routingCreateForm [name="visualRulesJson"]');
          const hiddenPdfName = document.querySelector('#routingCreateForm [name="samplePdfName"]');
          const hiddenPdfBase64 = document.querySelector('#routingCreateForm [name="samplePdfBase64"]');
          hiddenPdfName.value = state.routingEditor.samplePdfName || '';
          hiddenPdfBase64.value = state.routingEditor.samplePdfBase64 || '';
          hiddenRules.value = JSON.stringify(getRoutingVisualRulePayload());

          if (!state.routingEditor.visualRule) {
            summary.textContent = state.routingEditor.samplePdfName
              ? 'Sample loaded. Draw a rectangle on the page marker region.'
              : 'No visual rule yet. Load a PDF and drag a rectangle over the page marker region.';
            matched.textContent = '';
            return;
          }

          const selectedRouteType = getRoutingSelectedRouteType();
          const defaultRouteType = getRoutingDefaultRouteType();
          summary.textContent =
            'Selection routes ' + selectedRouteType + ', all other pages route ' + defaultRouteType +
            '. Sample text: "' + (state.routingEditor.visualRule.expectedText || '').slice(0, 120) + '"';
          matched.textContent =
            state.routingEditor.matchingPageNumbers.length > 0
              ? 'Matching sample pages: ' + state.routingEditor.matchingPageNumbers.join(', ')
              : 'No matching sample pages found for the current rectangle.';
        }

        function renderRoutingPageButtons() {
          const wrap = document.getElementById('routingPageButtons');
          wrap.innerHTML = '';
          state.routingEditor.pages.forEach((page) => {
            const button = document.createElement('button');
            button.type = 'button';
            button.textContent = 'Page ' + page.pageNumber;
            if (page.pageNumber === state.routingEditor.currentPageNumber) {
              button.classList.add('active');
            }
            if (state.routingEditor.matchingPageNumbers.includes(page.pageNumber)) {
              button.classList.add('match');
            }
            button.addEventListener('click', async () => {
              state.routingEditor.currentPageNumber = page.pageNumber;
              await renderRoutingPdfPage();
            });
            wrap.appendChild(button);
          });
        }

        async function renderRoutingPdfPage() {
          if (!state.routingEditor.pdfDoc) {
            return;
          }
          const page = await state.routingEditor.pdfDoc.getPage(state.routingEditor.currentPageNumber);
          const viewport = page.getViewport({ scale: 1.25 });
          const canvas = document.getElementById('routingPdfCanvas');
          const context = canvas.getContext('2d');
          canvas.width = viewport.width;
          canvas.height = viewport.height;
          await page.render({ canvasContext: context, viewport }).promise;
          renderRoutingSelectionOverlay();
          renderRoutingPageButtons();
          updateRoutingSummary();
        }

        async function loadRoutingPdfFromBase64(base64, fileName, existingRule) {
          if (!window.pdfjsLib) {
            throw new Error('PDF_RENDERER_UNAVAILABLE');
          }
          const pdfDoc = await window.pdfjsLib.getDocument({ data: base64ToUint8Array(base64) }).promise;
          const pages = [];
          for (let pageNumber = 1; pageNumber <= pdfDoc.numPages; pageNumber += 1) {
            const page = await pdfDoc.getPage(pageNumber);
            const viewport = page.getViewport({ scale: 1 });
            const textContent = await page.getTextContent();
            const textItems = (textContent.items || []).reduce((items, item) => {
              if (!item || typeof item.str !== 'string' || !Array.isArray(item.transform)) {
                return items;
              }
              const text = String(item.str || '').replace(/\s+/g, ' ').trim();
              if (!text) {
                return items;
              }
              const x = Number(item.transform[4] || 0);
              const height = Number(item.height || 10) || 10;
              const y = viewport.height - Number(item.transform[5] || 0) - height;
              items.push({
                text,
                x,
                y,
                width: Number(item.width || 0),
                height
              });
              return items;
            }, []);
            pages.push({
              pageNumber,
              width: viewport.width,
              height: viewport.height,
              textItems
            });
          }

          state.routingEditor.pdfDoc = pdfDoc;
          state.routingEditor.pages = pages;
          state.routingEditor.samplePdfName = fileName || '';
          state.routingEditor.samplePdfBase64 = base64 || '';
          state.routingEditor.currentPageNumber =
            existingRule && existingRule.samplePageNumber ? existingRule.samplePageNumber : 1;
          state.routingEditor.visualRule = existingRule
            ? {
                id: existingRule.id || '',
                samplePageNumber: existingRule.samplePageNumber,
                matchMode: existingRule.matchMode || 'CONTAINS',
                expectedText: existingRule.expectedText || '',
                expectedWords: existingRule.expectedWords || [],
                rect: existingRule.rect
              }
            : null;
          state.routingEditor.matchingPageNumbers = state.routingEditor.visualRule
            ? evaluateRoutingMatches(state.routingEditor.visualRule)
            : [];
          await renderRoutingPdfPage();
        }

        async function loadRoutingPdfFile(file) {
          if (!file) {
            throw new Error('SAMPLE_PDF_REQUIRED');
          }
          const buffer = await readFileAsArrayBuffer(file);
          const base64 = arrayBufferToBase64(buffer);
          await loadRoutingPdfFromBase64(base64, file.name, null);
        }

        function resetRoutingEditor(keepFile) {
          state.routingEditor.visualRule = null;
          state.routingEditor.matchingPageNumbers = [];
          if (!keepFile) {
            state.routingEditor.pdfDoc = null;
            state.routingEditor.pages = [];
            state.routingEditor.currentPageNumber = 1;
            state.routingEditor.samplePdfName = '';
            state.routingEditor.samplePdfBase64 = '';
            const canvas = document.getElementById('routingPdfCanvas');
            const context = canvas.getContext('2d');
            context.clearRect(0, 0, canvas.width, canvas.height);
            canvas.width = 0;
            canvas.height = 0;
          }
          renderRoutingSelectionOverlay();
          renderRoutingPageButtons();
          updateRoutingSummary();
        }

        function applyRoutingRectSelection(rect) {
          const page = state.routingEditor.pages.find((entry) => entry.pageNumber === state.routingEditor.currentPageNumber);
          if (!page) {
            return;
          }
          const expectedText = extractPageTextByRect(page, rect);
          if (!expectedText) {
            status('No text detected in that region. Choose a more specific area.', 'danger');
            return;
          }
          const expectedWords = expectedText.split(' ').filter(Boolean).slice(0, 12);
          state.routingEditor.visualRule = {
            id: state.routingEditor.visualRule ? state.routingEditor.visualRule.id || '' : '',
            samplePageNumber: page.pageNumber,
            matchMode: 'CONTAINS',
            expectedText,
            expectedWords,
            rect
          };
          state.routingEditor.matchingPageNumbers = evaluateRoutingMatches(state.routingEditor.visualRule);
          renderRoutingSelectionOverlay();
          renderRoutingPageButtons();
          updateRoutingSummary();
        }

        function renderAdDiscoveryLists() {
          setMultiSelectOptions(
            'adUsersSelect',
            state.adDiscovery.users.map((entry) => ({ value: entry.id, label: entry.username + ' - ' + entry.displayName }))
          );
          setMultiSelectOptions(
            'adGroupsSelect',
            state.adDiscovery.groups.map((entry) => ({
              value: entry.id,
              label: entry.name + ' (' + (entry.memberUsernames || []).length + ' members)'
            }))
          );
          setMultiSelectOptions(
            'adSmbSharesSelect',
            state.adDiscovery.smbShares.map((entry) => ({ value: entry.id, label: entry.path }))
          );
          setMultiSelectOptions(
            'adPrintersSelect',
            state.adDiscovery.printers.map((entry) => ({
              value: entry.id,
              label: entry.name + ' [' + (entry.type || 'auto') + ']'
            }))
          );
        }

        async function refreshAdSyncConfig() {
          const config = await api('/admin/config/ad-sync', 'GET');
          document.querySelector('#adConfigForm [name=\"enabled\"]').checked = Boolean(config.enabled);
          document.getElementById('adServerUrl').value = config.serverUrl || '';
          document.getElementById('adDomain').value = config.domain || '';
          document.getElementById('adBaseDn').value = config.baseDn || '';
          document.getElementById('adBindUsername').value = config.bindUsername || '';
          document.getElementById('adBindSecretRef').value = config.bindSecretRef || '';
          if (!document.getElementById('adDefaultSmbSecretRef').value) {
            document.getElementById('adDefaultSmbSecretRef').value = config.bindSecretRef || '';
          }
        }

        function updateKpis() {
          const admins = state.users.filter((u) => (u.roles || []).includes('ADMIN')).length;
          const managed = state.users.filter((u) => !(u.roles || []).includes('ADMIN')).length;
          document.getElementById('kpiAdmins').textContent = String(admins);
          document.getElementById('kpiUsers').textContent = String(managed);
          document.getElementById('kpiGroups').textContent = String(state.groups.length);
        }

        async function refreshUsers() {
          const users = await api('/admin/users', 'GET');
          state.users = users;
          const admins = users.filter((u) => (u.roles || []).includes('ADMIN'));
          const managed = users.filter((u) => !(u.roles || []).includes('ADMIN'));

          fillTable('adminsTable', admins, (u) => {
            const tr = document.createElement('tr');
            const del = async () => {
              await api('/admin/users/' + u.id, 'DELETE');
              await refreshUsers();
              await refreshMemberships();
            };
            tr.innerHTML = '<td><code>' + u.id + '</code></td><td>' + u.username + '</td><td><span class="badge">ADMIN</span></td><td></td>';
            tr.children[3].appendChild(buildActionButtons([{ label: 'Delete', onClick: del }]));
            return tr;
          }, 4);

          fillTable('usersTable', managed, (u) => {
            const tr = document.createElement('tr');
            const toggleRemote = async () => {
              await api('/admin/users/' + u.id, 'PATCH', { isRemoteEnabled: !u.isRemoteEnabled });
              await refreshUsers();
            };
            const del = async () => {
              await api('/admin/users/' + u.id, 'DELETE');
              await refreshUsers();
              await refreshMemberships();
            };
            tr.innerHTML = '<td><code>' + u.id + '</code></td><td>' + u.username + '</td><td>' + String(Boolean(u.isRemoteEnabled)) + '</td><td></td>';
            tr.children[3].appendChild(
              buildActionButtons([
                { label: 'Toggle remote', onClick: toggleRemote, secondary: true },
                { label: 'Delete', onClick: del }
              ])
            );
            return tr;
          }, 4);

          updateKpis();
          refreshSelectors();
        }

        async function refreshGroups() {
          const groups = await api('/admin/groups', 'GET');
          state.groups = groups;
          fillTable('groupsTable', groups, (g) => {
            const tr = document.createElement('tr');
            const del = async () => {
              await api('/admin/groups/' + g.id, 'DELETE');
              await refreshGroups();
              await refreshMemberships();
            };
            tr.innerHTML = '<td><code>' + g.id + '</code></td><td>' + g.name + '</td><td>' + (g.description || '-') + '</td><td></td>';
            tr.children[3].appendChild(buildActionButtons([{ label: 'Delete', onClick: del }]));
            return tr;
          }, 4);
          updateKpis();
          refreshSelectors();
        }

        async function refreshMemberships() {
          const records = await api('/admin/group-memberships', 'GET');
          state.memberships = records;
          fillTable('membershipsTable', records, (m) => {
            const tr = document.createElement('tr');
            const del = async () => {
              await api('/admin/group-memberships/' + m.groupId + '/' + m.userId, 'DELETE');
              await refreshMemberships();
            };
            tr.innerHTML = '<td><code>' + m.groupId + '</code></td><td><code>' + m.userId + '</code></td><td></td>';
            tr.children[2].appendChild(buildActionButtons([{ label: 'Delete', onClick: del }]));
            return tr;
          }, 3);
        }

        async function refreshSmb() {
          const rows = await api('/admin/config/smb-sources', 'GET');
          state.smbSources = rows;
          fillTable('smbTable', rows, (r) => {
            const tr = document.createElement('tr');
            const assign = async () => {
              document.getElementById('assignSmbSourceSelect').value = r.id;
              activatePanel('assignments');
            };
            const del = async () => {
              await api('/admin/config/smb-sources/' + r.id, 'DELETE');
              await refreshSmb();
            };
            tr.innerHTML =
              '<td><code>' + r.id + '</code></td><td><code>' + r.path + '</code></td>' +
              '<td>' + r.domainUsername + ' / ' + r.secretRef + '</td>' +
              '<td>user:' + (r.ownerUserId || '-') + '<br/>group:' + (r.ownerGroupId || '-') + '</td><td></td>';
            tr.children[4].appendChild(
              buildActionButtons([
                { label: 'Assign', onClick: assign, secondary: true },
                { label: 'Delete', onClick: del }
              ])
            );
            return tr;
          }, 5);
          refreshSelectors();
        }

        async function refreshMasks() {
          const rows = await api('/admin/config/filename-masks', 'GET');
          fillTable('masksTable', rows, (r) => {
            const tr = document.createElement('tr');
            const del = async () => {
              await api('/admin/config/filename-masks/' + r.id, 'DELETE');
              await refreshMasks();
            };
            tr.innerHTML =
              '<td><code>' + r.id + '</code></td><td><code>' + r.pattern + '</code></td><td>' + String(r.isRegex) + '</td>' +
              '<td>user:' + (r.ownerUserId || '-') + '<br/>group:' + (r.ownerGroupId || '-') + '</td><td></td>';
            tr.children[4].appendChild(buildActionButtons([{ label: 'Delete', onClick: del }]));
            return tr;
          }, 5);
        }

        async function refreshPrinters() {
          const rows = await api('/admin/config/printers', 'GET');
          state.printers = rows;
          fillTable('printersTable', rows, (r) => {
            const tr = document.createElement('tr');
            const assign = async () => {
              const slot = r.type === 'A4' ? 'assignA4Select' : 'assignThermalSelect';
              document.getElementById(slot).value = r.id;
              activatePanel('assignments');
            };
            const del = async () => {
              await api('/admin/config/printers/' + r.id, 'DELETE');
              await refreshPrinters();
              await refreshAssignments();
            };
            tr.innerHTML =
              '<td><code>' + r.id + '</code></td><td>' + r.name + '</td><td>' + r.type + '</td><td><code>' + r.targetUri + '</code></td>' +
              '<td>' + (r.domainUsername || '-') + ' / ' + (r.secretRef || '-') + '</td><td></td>';
            tr.children[5].appendChild(
              buildActionButtons([
                { label: 'Assign', onClick: assign, secondary: true },
                { label: 'Delete', onClick: del }
              ])
            );
            return tr;
          }, 6);
          refreshSelectors();
        }

        async function refreshRouting() {
          const rows = await api('/admin/config/routing-profiles', 'GET');
          state.routingProfiles = rows;
          fillTable('routingTable', rows, (r) => {
            const tr = document.createElement('tr');
            const edit = async () => {
              const form = getRoutingForm();
              form.querySelector('[name="routingProfileId"]').value = r.id;
              form.querySelector('[name="name"]').value = r.name || '';
              form.querySelector('[name="defaultRouteType"]').value = r.defaultRouteType || 'A4';
              form.querySelector('[name="patterns"]').value = (r.thermalLabelPatterns || []).join(', ');
              form.querySelector('[name="fallbackPrinterId"]').value = r.fallbackPrinterId || '';
              form.querySelector('[name="ownerUserId"]').value = r.ownerUserId || '';
              form.querySelector('[name="ownerGroupId"]').value = r.ownerGroupId || '';
              if (r.samplePdfBase64) {
                await loadRoutingPdfFromBase64(
                  r.samplePdfBase64,
                  r.samplePdfName || (r.name || 'sample.pdf'),
                  (r.visualRules || [])[0] || null
                );
              } else {
                resetRoutingEditor(false);
              }
              updateRoutingSummary();
            };
            const assign = async () => {
              document.getElementById('assignRoutingSelect').value = r.id;
              activatePanel('assignments');
            };
            const del = async () => {
              await api('/admin/config/routing-profiles/' + r.id, 'DELETE');
              await refreshRouting();
            };
            const visualRule = (r.visualRules || [])[0] || null;
            tr.innerHTML =
              '<td><code>' + r.id + '</code></td><td>' + r.name + '</td><td>' +
              (visualRule
                ? 'page ' + visualRule.samplePageNumber + ' -> ' + visualRule.routeType + '<br/><span class="muted">' + (r.samplePdfName || 'sample pdf') + '</span>'
                : '<span class="muted">patterns only</span>') +
              '<br/><code>' + JSON.stringify(r.thermalLabelPatterns || []) + '</code></td>' +
              '<td>' + (r.fallbackPrinterId || '-') + '</td><td>user:' + (r.ownerUserId || '-') + '<br/>group:' + (r.ownerGroupId || '-') + '</td><td></td>';
            tr.children[5].appendChild(
              buildActionButtons([
                { label: 'Edit', onClick: edit, secondary: true },
                { label: 'Assign', onClick: assign, secondary: true },
                { label: 'Delete', onClick: del }
              ])
            );
            return tr;
          }, 6);
          refreshSelectors();
        }

        async function refreshVisualProfiles() {
          const rows = await api('/admin/config/visual-profiles', 'GET');
          fillTable('visualProfilesTable', rows, (r) => {
            const tr = document.createElement('tr');
            const del = async () => {
              await api('/admin/config/visual-profiles/' + r.id, 'DELETE');
              await refreshVisualProfiles();
            };
            tr.innerHTML =
              '<td><code>' + r.id + '</code></td><td>' + r.name + '</td><td>' + r.matchMode + '</td>' +
              '<td>' + (r.routeType || '-') + ' / ' + (r.printerId || '-') + '</td>' +
              '<td>user:' + (r.ownerUserId || '-') + '<br/>group:' + (r.ownerGroupId || '-') + '</td><td></td>';
            tr.children[5].appendChild(buildActionButtons([{ label: 'Delete', onClick: del }]));
            return tr;
          }, 6);
        }

        async function refreshAssignments() {
          const rows = await api('/admin/config/user-printer-assignments', 'GET');
          fillTable('assignmentsTable', rows, (r) => {
            const tr = document.createElement('tr');
            const del = async () => {
              await api('/admin/config/user-printer-assignments/' + r.userId, 'DELETE');
              await refreshAssignments();
            };
            tr.innerHTML =
              '<td><code>' + r.userId + '</code></td><td>' + (r.a4PrinterId || '-') + '</td><td>' + (r.thermalPrinterId || '-') + '</td><td></td>';
            tr.children[3].appendChild(buildActionButtons([{ label: 'Delete', onClick: del }]));
            return tr;
          }, 4);
        }

        async function refreshOcr() {
          const global = await api('/admin/config/ocr/global', 'GET');
          document.getElementById('ocrGlobalPreview').textContent = JSON.stringify(global, null, 2);
          const rows = await api('/admin/config/ocr/overrides', 'GET');
          fillTable('ocrOverridesTable', rows, (r) => {
            const tr = document.createElement('tr');
            const del = async () => {
              await api('/admin/config/ocr/overrides/' + r.userId, 'DELETE');
              await refreshOcr();
            };
            tr.innerHTML =
              '<td><code>' + r.userId + '</code></td><td>' + (r.provider || '-') + '</td><td><code>' + JSON.stringify(r.config || {}) + '</code></td><td></td>';
            tr.children[3].appendChild(buildActionButtons([{ label: 'Delete', onClick: del }]));
            return tr;
          }, 4);
        }

        async function refreshSystemSettings() {
          const settings = await api('/admin/config/system-settings', 'GET');
          state.systemSettings = settings || null;
          if (!settings) return;
          document.getElementById('globalSmbUsername').value = settings.globalSmbDomainUsername || '';
          document.getElementById('globalSmbSecretRef').value = settings.globalSmbSecretRef || '';
          document.getElementById('globalPrinterUsername').value = settings.globalPrinterDomainUsername || '';
          document.getElementById('globalPrinterSecretRef').value = settings.globalPrinterSecretRef || '';
          document.getElementById('workerPollIntervalMs').value = String(settings.workerPollIntervalMs || 5000);
          document.getElementById('smtpEnabled').checked = Boolean(settings.smtpEnabled);
          document.getElementById('smtpHost').value = settings.smtpHost || '';
          document.getElementById('smtpPort').value = String(settings.smtpPort || 25);
          document.getElementById('smtpSecure').checked = Boolean(settings.smtpSecure);
          document.getElementById('smtpUsername').value = settings.smtpUsername || '';
          document.getElementById('smtpSecretRef').value = settings.smtpSecretRef || '';
          document.getElementById('smtpFrom').value = settings.smtpFrom || '';
          document.getElementById('smtpTo').value = Array.isArray(settings.smtpTo) ? settings.smtpTo.join(', ') : '';
        }

        async function refreshOperations() {
          const workerStatus = await api('/worker/pipeline/status', 'GET');
          const jobs = await api('/worker/pipeline/jobs', 'GET');
          const notificationAttempts = await api('/worker/pipeline/notifications?limit=50', 'GET');
          const logs = await api('/admin/logs?limit=50', 'GET');

          state.pipelineJobs = jobs || [];
          state.notificationAttempts = notificationAttempts || [];
          state.auditLogs = logs || [];
          document.getElementById('workerStatusPreview').textContent = JSON.stringify(workerStatus || {}, null, 2);

          fillTable('operationsJobsTable', state.pipelineJobs, (job) => {
            const tr = document.createElement('tr');
            const loadPages = async () => {
              const pages = await api('/worker/pipeline/jobs/' + job.id + '/pages', 'GET');
              state.pipelineJobPages[job.id] = pages || [];
              await refreshOperations();
            };
            const cancelJob = async () => {
              await api('/worker/pipeline/jobs/' + job.id + '/cancel', 'POST', {});
              await refreshOperations();
            };
            const retryJob = async () => {
              await api('/worker/pipeline/jobs/' + job.id + '/retry', 'POST', {});
              await refreshOperations();
            };
            const pageSummary = (state.pipelineJobPages[job.id] || [])
              .map((page) => 'p' + page.pageNumber + ':' + page.routeType + ':' + page.status)
              .join(', ');
            tr.innerHTML =
              '<td><code>' + job.id + '</code></td>' +
              '<td><code>' + job.filePath + '</code></td>' +
              '<td>' + job.status + (job.isCancelled ? ' / cancelled' : '') + (job.errorMessage ? '<br/><span class="danger">' + job.errorMessage + '</span>' : '') + '</td>' +
              '<td>' + (pageSummary || '<span class="muted">not loaded</span>') + '</td><td></td>';
            tr.children[4].appendChild(
              buildActionButtons([
                { label: 'Pages', onClick: loadPages, secondary: true },
                { label: 'Retry', onClick: retryJob, secondary: true },
                { label: 'Cancel', onClick: cancelJob }
              ])
            );
            return tr;
          }, 5);

          fillTable('notificationAttemptsTable', state.notificationAttempts, (attempt) => {
            const tr = document.createElement('tr');
            const createdAt = attempt.createdAt ? new Date(attempt.createdAt).toLocaleString() : '-';
            tr.innerHTML =
              '<td>' + createdAt + '</td>' +
              '<td>' + attempt.category + '</td>' +
              '<td>' + attempt.subject + '</td>' +
              '<td>' + attempt.status + '</td>' +
              '<td>' +
              '<code>' +
              JSON.stringify({ dedupeKey: attempt.dedupeKey, recipients: attempt.recipientCount, error: attempt.errorMessage }) +
              '</code></td>';
            return tr;
          }, 5);

          fillTable('auditLogsTable', state.auditLogs, (record) => {
            const tr = document.createElement('tr');
            const createdAt = record.createdAt ? new Date(record.createdAt).toLocaleString() : '-';
            tr.innerHTML =
              '<td>' + createdAt + '</td>' +
              '<td>' + record.action + '</td>' +
              '<td>' + record.status + '</td>' +
              '<td>' + (record.actorUsername || record.actorUserId || '-') + '</td>' +
              '<td><code>' + JSON.stringify(record.metadata || {}) + '</code></td>';
            return tr;
          }, 5);
        }

        async function refreshAll() {
          if (!state.authToken) return;
          await Promise.all([
            refreshAdSyncConfig(),
            refreshUsers(),
            refreshGroups(),
            refreshMemberships(),
            refreshSmb(),
            refreshMasks(),
            refreshPrinters(),
            refreshRouting(),
            refreshVisualProfiles(),
            refreshAssignments(),
            refreshOcr(),
            refreshSystemSettings(),
            refreshOperations()
          ]);
        }

        function bindForm(id, handler) {
          const form = document.getElementById(id);
          if (!form) return;
          form.addEventListener('submit', async (event) => {
            event.preventDefault();
            try {
              await handler(new FormData(form));
              status('Saved', 'ok');
            } catch (error) {
              status('Error: ' + String(error && error.message ? error.message : 'UNKNOWN'), 'danger');
            }
          });
        }

        function activatePanel(key) {
          document.querySelectorAll('.panel').forEach((panel) => panel.classList.remove('active'));
          document.querySelectorAll('.step-nav button').forEach((button) => button.classList.remove('active'));
          const panel = document.querySelector('[data-panel="' + key + '"]');
          if (panel) panel.classList.add('active');
          const button = document.querySelector('[data-step="' + key + '"]');
          if (button) button.classList.add('active');
        }

        function initStepNav() {
          const nav = document.getElementById('stepNav');
          nav.innerHTML = '';
          navSteps.forEach((step, index) => {
            const button = document.createElement('button');
            button.type = 'button';
            button.dataset.step = step[0];
            button.className = index === 0 ? 'active' : '';
            button.textContent = t(step[1], step[2]);
            button.addEventListener('click', () => activatePanel(step[0]));
            nav.appendChild(button);
          });
        }

        function applyTheme(theme) {
          state.theme = theme || 'system';
          localStorage.setItem(STORAGE_KEYS.theme, state.theme);
          if (state.theme === 'system') {
            const prefersDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
            document.documentElement.setAttribute('data-theme', prefersDark ? 'dark' : 'light');
          } else {
            document.documentElement.setAttribute('data-theme', state.theme);
          }
          document.getElementById('themeSelect').value = state.theme;
        }

        async function loadMessages(locale) {
          const response = await fetch('/i18n/messages?locale=' + encodeURIComponent(locale));
          const payload = await response.json();
          state.messages = payload.messages || {};
          state.locale = payload.locale || locale;
          localStorage.setItem(STORAGE_KEYS.locale, state.locale);
          document.getElementById('localeSelect').value = state.locale;
          document.querySelectorAll('[data-i18n]').forEach((node) => {
            const key = node.getAttribute('data-i18n');
            if (!key) return;
            const value = t(key, '');
            if (value) node.textContent = value;
          });
          applyStaticTranslations();
          initStepNav();
          refreshSelectors();
          if (state.authToken) {
            document.getElementById('authSessionBadge').textContent =
              t('auth.signedInAs', 'Signed in as') + ' ' + (state.authUser || 'admin');
          }
        }

        function applyStaticTranslations() {
          const textBySelector = {
            '.header p': ['header.guidedFlow', 'Guided setup flow: admin access, users/groups, AD sync, visual profile, printers, SMB, routing, assignments.'],
            '.sidebar .card.stack h2': ['settings.title', 'Preferences'],
            '.sidebar .card:nth-of-type(2) h2': ['setup.title', 'Setup flow'],
            '.sidebar .card:nth-of-type(3) h2': ['status.global', 'Global status'],
            '#globalStatus': ['status.ready', 'Ready'],
            '.kpi .card:nth-child(1) .muted': ['kpi.admins', 'Admins'],
            '.kpi .card:nth-child(2) .muted': ['kpi.managedUsers', 'Managed users'],
            '.kpi .card:nth-child(3) .muted': ['kpi.groups', 'Groups'],
            '.panel[data-panel="admin-accounts"] .card.stack:nth-of-type(1) h2': ['admin.accounts', 'Admin accounts'],
            '.panel[data-panel="admin-accounts"] .card.stack:nth-of-type(1) p': ['admin.accounts.help', 'Separate from managed users and groups.'],
            '#adminCreateForm button': ['admin.create', 'Create admin'],
            '.panel[data-panel="admin-accounts"] .card.stack:nth-of-type(2) h2': ['usersGroups.stepTitle', 'Step 2: Users & Groups'],
            '.panel[data-panel="admin-accounts"] .card.stack:nth-of-type(2) p': ['usersGroups.help', 'These users are managed identities for assignment. They do not need to sign in to this app.'],
            '#userCreateForm h3': ['usersGroups.createUser', 'Create managed user'],
            '#groupCreateForm h3': ['usersGroups.createGroup', 'Create group'],
            '#groupMembershipForm button': ['usersGroups.addMembership', 'Add membership'],
            '#usersTableWrap thead th:nth-child(1)': ['table.id', 'ID'],
            '#usersTableWrap thead th:nth-child(2)': ['table.user', 'User'],
            '#usersTableWrap thead th:nth-child(3)': ['table.remote', 'Remote'],
            '#usersTableWrap thead th:nth-child(4)': ['table.actions', 'Actions'],
            '#groupsTableWrap thead th:nth-child(1)': ['table.id', 'ID'],
            '#groupsTableWrap thead th:nth-child(2)': ['table.group', 'Group'],
            '#groupsTableWrap thead th:nth-child(3)': ['table.description', 'Description'],
            '#groupsTableWrap thead th:nth-child(4)': ['table.actions', 'Actions'],
            '#membershipsTableWrap thead th:nth-child(1)': ['table.group', 'Group'],
            '#membershipsTableWrap thead th:nth-child(2)': ['table.user', 'User'],
            '#membershipsTableWrap thead th:nth-child(3)': ['table.actions', 'Actions']
          };

          Object.entries(textBySelector).forEach(([selector, spec]) => {
            const node = document.querySelector(selector);
            if (!node) return;
            node.textContent = t(spec[0], spec[1]);
          });

          const placeholders = [
            ['#adminCreateForm input[name="username"]', 'placeholder.adminUsername', 'admin username'],
            ['#adminCreateForm input[name="password"]', 'placeholder.adminPassword', 'admin password'],
            ['#userCreateForm input[name="username"]', 'placeholder.userIdentifier', 'user identifier'],
            ['#groupCreateForm input[name="name"]', 'placeholder.groupName', 'group name'],
            ['#groupCreateForm input[name="description"]', 'placeholder.description', 'description']
          ];
          placeholders.forEach((entry) => {
            const node = document.querySelector(entry[0]);
            if (!node) return;
            node.placeholder = t(entry[1], entry[2]);
          });
        }

        function wireVisualDropzone() {
          const dropzone = document.getElementById('visualDropzone');
          const preview = document.getElementById('visualPreview');
          const textarea = document.getElementById('visualSnippet');
          const fileInput = document.getElementById('visualFileInput');

          function setSnippet(base64) {
            textarea.value = base64;
            preview.src = base64;
          }

          async function readFile(file) {
            const reader = new FileReader();
            return new Promise((resolve, reject) => {
              reader.onload = () => resolve(String(reader.result || ''));
              reader.onerror = () => reject(new Error('FILE_READ_FAILED'));
              reader.readAsDataURL(file);
            });
          }

          async function consumeFile(file) {
            if (!file || !file.type.startsWith('image/')) return;
            const dataUrl = await readFile(file);
            setSnippet(dataUrl);
          }

          ['dragenter', 'dragover'].forEach((name) => {
            dropzone.addEventListener(name, (event) => {
              event.preventDefault();
              dropzone.classList.add('active');
            });
          });

          ['dragleave', 'drop'].forEach((name) => {
            dropzone.addEventListener(name, (event) => {
              event.preventDefault();
              dropzone.classList.remove('active');
            });
          });

          dropzone.addEventListener('drop', async (event) => {
            const file = event.dataTransfer && event.dataTransfer.files ? event.dataTransfer.files[0] : null;
            if (file) await consumeFile(file);
          });

          document.addEventListener('paste', async (event) => {
            const items = event.clipboardData ? event.clipboardData.items : [];
            for (let idx = 0; idx < items.length; idx += 1) {
              if (items[idx].type.indexOf('image') === 0) {
                const file = items[idx].getAsFile();
                if (file) {
                  await consumeFile(file);
                  break;
                }
              }
            }
          });

          fileInput.addEventListener('change', async () => {
            const file = fileInput.files && fileInput.files[0] ? fileInput.files[0] : null;
            if (file) await consumeFile(file);
          });

          document.getElementById('visualChooseFile').addEventListener('click', () => fileInput.click());
          document.getElementById('visualReplace').addEventListener('click', () => {
            textarea.value = '';
            preview.removeAttribute('src');
            fileInput.value = '';
          });
        }

        function wireRoutingTestZone() {
          const fileInput = document.getElementById('routingTestFile');
          const textArea = document.getElementById('routingTestText');
          const meta = document.getElementById('routingTestMeta');
          const dropzone = document.getElementById('routingDropzone');
          if (!fileInput || !textArea || !meta || !dropzone) {
            return;
          }

          function setMeta(text) {
            meta.textContent = text;
          }

          fileInput.addEventListener('change', () => {
            const file = fileInput.files && fileInput.files[0] ? fileInput.files[0] : null;
            if (!file) return;
            setMeta('Loaded file: ' + file.name + ' (' + file.size + ' bytes)');
          });

          ['dragenter', 'dragover'].forEach((name) => {
            dropzone.addEventListener(name, (event) => {
              event.preventDefault();
              dropzone.classList.add('active');
            });
          });

          ['dragleave', 'drop'].forEach((name) => {
            dropzone.addEventListener(name, (event) => {
              event.preventDefault();
              dropzone.classList.remove('active');
            });
          });

          dropzone.addEventListener('drop', (event) => {
            const file = event.dataTransfer && event.dataTransfer.files ? event.dataTransfer.files[0] : null;
            if (file) {
              setMeta('Loaded file: ' + file.name + ' (' + file.size + ' bytes)');
              return;
            }
            const text = event.dataTransfer ? event.dataTransfer.getData('text/plain') : '';
            if (text) {
              textArea.value = text;
              setMeta('Loaded dropped text (' + text.length + ' chars)');
            }
          });

          textArea.addEventListener('input', () => {
            if (textArea.value.trim()) {
              setMeta('Test payload text staged (' + textArea.value.trim().length + ' chars)');
            }
          });
        }

        async function loadBootstrapState() {
          const response = await fetch('/auth/bootstrap-status');
          const payload = await response.json();
          state.requiresBootstrap = Boolean(payload && payload.requiresBootstrap);
          document.getElementById('bootstrapPanel').style.display = state.requiresBootstrap ? 'block' : 'none';
        }

        bindForm('bootstrapForm', async (data) => {
          const headers = { 'content-type': 'application/json' };
          const token = String(data.get('token') || '').trim();
          if (token) headers['x-bootstrap-token'] = token;
          const response = await fetch('/auth/bootstrap-admin', {
            method: 'POST',
            headers,
            body: JSON.stringify({
              username: String(data.get('username') || ''),
              password: String(data.get('password') || '')
            })
          });
          const payload = await response.json();
          if (!response.ok) throw new Error(payload.error || 'BOOTSTRAP_FAILED');
          document.getElementById('bootstrapStatus').textContent = 'Initial admin created';
          await loadBootstrapState();
        });

        bindForm('loginForm', async (data) => {
          const response = await fetch('/auth/login', {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({
              username: String(data.get('username') || ''),
              password: String(data.get('password') || '')
            })
          });
          const payload = await response.json();
          if (!response.ok || !payload.accessToken) throw new Error(payload.error || 'LOGIN_FAILED');
          setSession(payload.accessToken, payload.user && payload.user.username ? payload.user.username : 'admin');
          document.getElementById('authStatus').textContent = 'Signed in';

          try {
            const prefs = await api('/me/preferences', 'GET');
            if (prefs && prefs.locale) {
              await loadMessages(prefs.locale);
            }
            if (prefs && prefs.theme) {
              applyTheme(prefs.theme);
            }
          } catch {
            await loadMessages(state.locale);
            applyTheme(state.theme);
          }

          await refreshAll();
        });

        document.getElementById('logoutButton').addEventListener('click', () => {
          setSession('', '');
          status('Signed out', 'muted');
        });

        bindForm('adminCreateForm', async (data) => {
          await api('/admin/users', 'POST', {
            username: String(data.get('username') || ''),
            password: String(data.get('password') || ''),
            roles: ['ADMIN'],
            isRemoteEnabled: false
          });
          document.getElementById('adminCreateForm').reset();
          await refreshUsers();
        });

        bindForm('userCreateForm', async (data) => {
          await api('/admin/users', 'POST', {
            username: String(data.get('username') || ''),
            password: ensurePassword(data.get('password')),
            roles: ['USER'],
            isRemoteEnabled: Boolean(data.get('isRemoteEnabled'))
          });
          document.getElementById('userCreateForm').reset();
          await refreshUsers();
        });

        bindForm('groupCreateForm', async (data) => {
          await api('/admin/groups', 'POST', {
            name: String(data.get('name') || ''),
            description: optional(data.get('description')),
            isActive: true
          });
          document.getElementById('groupCreateForm').reset();
          await refreshGroups();
        });

        bindForm('groupMembershipForm', async (data) => {
          await api('/admin/group-memberships', 'POST', {
            groupId: String(data.get('groupId') || ''),
            userId: String(data.get('userId') || '')
          });
          await refreshMemberships();
        });

        bindForm('adConfigForm', async (data) => {
          await api('/admin/config/ad-sync', 'PUT', {
            enabled: Boolean(data.get('enabled')),
            serverUrl: String(data.get('serverUrl') || ''),
            domain: String(data.get('domain') || ''),
            baseDn: String(data.get('baseDn') || ''),
            bindUsername: String(data.get('bindUsername') || ''),
            bindSecretRef: String(data.get('bindSecretRef') || '')
          });
          await refreshAdSyncConfig();
          document.getElementById('adSyncStatus').textContent = 'AD sync config saved.';
          document.getElementById('adSyncStatus').className = 'ok';
        });

        document.getElementById('adDiscoverButton').addEventListener('click', async () => {
          try {
            const bindPassword = String(document.getElementById('adBindPassword').value || '').trim();
            const payload = bindPassword ? { bindPassword } : {};
            const snapshot = await api('/admin/config/ad-sync/discover', 'POST', payload);
            state.adDiscovery = snapshot || { users: [], groups: [], smbShares: [], printers: [] };
            renderAdDiscoveryLists();
            document.getElementById('adSyncStatus').textContent =
              'Discovered users=' + state.adDiscovery.users.length +
              ', groups=' + state.adDiscovery.groups.length +
              ', shares=' + state.adDiscovery.smbShares.length +
              ', printers=' + state.adDiscovery.printers.length;
            document.getElementById('adSyncStatus').className = 'ok';
          } catch (error) {
            document.getElementById('adSyncStatus').textContent = 'Discovery failed: ' + String(error.message || error);
            document.getElementById('adSyncStatus').className = 'danger';
          }
        });

        document.getElementById('adImportButton').addEventListener('click', async () => {
          try {
            const bindPassword = String(document.getElementById('adBindPassword').value || '').trim();
            const payload = {
              bindPassword: bindPassword || undefined,
              userIds: selectedValues('adUsersSelect'),
              groupIds: selectedValues('adGroupsSelect'),
              smbShareIds: selectedValues('adSmbSharesSelect'),
              printerIds: selectedValues('adPrintersSelect'),
              defaultSmbDomainUsername: String(document.getElementById('adDefaultSmbDomainUsername').value || ''),
              defaultSmbSecretRef: String(document.getElementById('adDefaultSmbSecretRef').value || '')
            };
            const result = await api('/admin/config/ad-sync/import', 'POST', payload);
            document.getElementById('adSyncStatus').textContent = 'Import complete: ' + JSON.stringify(result.created || {});
            document.getElementById('adSyncStatus').className = 'ok';
            await refreshAll();
          } catch (error) {
            document.getElementById('adSyncStatus').textContent = 'Import failed: ' + String(error.message || error);
            document.getElementById('adSyncStatus').className = 'danger';
          }
        });

        bindForm('visualProfileForm', async (data) => {
          await api('/admin/config/visual-profiles', 'POST', {
            name: String(data.get('name') || ''),
            matchMode: String(data.get('matchMode') || 'CONTAINS'),
            ownerUserId: optional(data.get('ownerUserId')),
            ownerGroupId: optional(data.get('ownerGroupId')),
            routeType: optional(data.get('routeType')),
            printerId: optional(data.get('printerId')),
            labels: splitCsv(data.get('labels')),
            snippetBase64: String(data.get('snippetBase64') || '').trim(),
            isActive: true
          });
          await refreshVisualProfiles();
        });

        bindForm('printerCreateForm', async (data) => {
          const globalUser = state.systemSettings ? state.systemSettings.globalPrinterDomainUsername || '' : '';
          const globalSecret = state.systemSettings ? state.systemSettings.globalPrinterSecretRef || '' : '';
          const domainUsername = String(data.get('domainUsername') || '').trim() || globalUser;
          const password = String(data.get('password') || '').trim();
          const secretRef = String(data.get('secretRef') || '').trim() || globalSecret;
          if (domainUsername && !isValidDomainUsername(domainUsername)) {
            throw new Error('INVALID_DOMAIN_USERNAME_FORMAT');
          }
          await api('/admin/config/printers', 'POST', {
            name: String(data.get('name') || ''),
            type: String(data.get('type') || 'A4'),
            targetUri: String(data.get('targetUri') || ''),
            domainUsername,
            password,
            secretRef,
            isActive: true
          });
          document.getElementById('printerCreateForm').reset();
          await refreshPrinters();
        });

        bindForm('smbCreateForm', async (data) => {
          const globalUser = state.systemSettings ? state.systemSettings.globalSmbDomainUsername || '' : '';
          const globalSecret = state.systemSettings ? state.systemSettings.globalSmbSecretRef || '' : '';
          const domainUsername = String(data.get('domainUsername') || '').trim() || globalUser;
          const password = String(data.get('password') || '').trim();
          const secretRef = String(data.get('secretRef') || '').trim() || globalSecret;
          if (!domainUsername || !isValidDomainUsername(domainUsername)) {
            throw new Error('INVALID_DOMAIN_USERNAME_FORMAT');
          }
          await api('/admin/config/smb-sources', 'POST', {
            path: String(data.get('path') || ''),
            domainUsername,
            password,
            secretRef,
            ownerUserId: optional(data.get('ownerUserId')),
            ownerGroupId: optional(data.get('ownerGroupId')),
            isActive: true
          });
          document.getElementById('smbCreateForm').reset();
          await refreshSmb();
        });

        bindForm('maskCreateForm', async (data) => {
          await api('/admin/config/filename-masks', 'POST', {
            pattern: String(data.get('pattern') || ''),
            isRegex: Boolean(data.get('isRegex')),
            ownerUserId: optional(data.get('ownerUserId')),
            ownerGroupId: optional(data.get('ownerGroupId')),
            isActive: true
          });
          await refreshMasks();
        });

        bindForm('routingCreateForm', async (data) => {
          const routingProfileId = String(data.get('routingProfileId') || '').trim();
          const payload = {
            name: String(data.get('name') || ''),
            defaultRouteType: String(data.get('defaultRouteType') || 'A4'),
            thermalLabelPatterns: splitCsv(data.get('patterns')),
            fallbackPrinterId: optional(data.get('fallbackPrinterId')),
            ownerUserId: optional(data.get('ownerUserId')),
            ownerGroupId: optional(data.get('ownerGroupId')),
            samplePdfName: optional(data.get('samplePdfName')),
            samplePdfBase64: optional(data.get('samplePdfBase64')),
            visualRules: JSON.parse(String(data.get('visualRulesJson') || '[]'))
          };
          if (routingProfileId) {
            await api('/admin/config/routing-profiles/' + routingProfileId, 'PATCH', payload);
          } else {
            await api('/admin/config/routing-profiles', 'POST', payload);
          }
          document.getElementById('routingCreateForm').reset();
          document.querySelector('#routingCreateForm [name="defaultRouteType"]').value = 'A4';
          resetRoutingEditor(false);
          await refreshRouting();
        });

        document.getElementById('routingLoadSample').addEventListener('click', async () => {
          const file = document.getElementById('routingSampleFile').files[0];
          await loadRoutingPdfFile(file);
          updateRoutingSummary();
        });

        document.getElementById('routingFlipSelection').addEventListener('click', () => {
          const select = document.querySelector('#routingCreateForm [name="defaultRouteType"]');
          select.value = select.value === 'A4' ? 'THERMAL' : 'A4';
          updateRoutingSummary();
          renderRoutingPageButtons();
        });

        document.getElementById('routingClearRule').addEventListener('click', () => {
          resetRoutingEditor(true);
        });

        document.querySelector('#routingCreateForm [name="defaultRouteType"]').addEventListener('change', () => {
          updateRoutingSummary();
          renderRoutingPageButtons();
        });

        const routingCanvas = document.getElementById('routingPdfCanvas');
        routingCanvas.addEventListener('mousedown', (event) => {
          if (!state.routingEditor.pdfDoc) {
            return;
          }
          const bounds = routingCanvas.getBoundingClientRect();
          state.routingEditor.dragStart = {
            x: Math.max(0, Math.min(bounds.width, event.clientX - bounds.left)),
            y: Math.max(0, Math.min(bounds.height, event.clientY - bounds.top))
          };
        });

        routingCanvas.addEventListener('mousemove', (event) => {
          if (!state.routingEditor.dragStart) {
            return;
          }
          const bounds = routingCanvas.getBoundingClientRect();
          const currentX = Math.max(0, Math.min(bounds.width, event.clientX - bounds.left));
          const currentY = Math.max(0, Math.min(bounds.height, event.clientY - bounds.top));
          const left = Math.min(state.routingEditor.dragStart.x, currentX);
          const top = Math.min(state.routingEditor.dragStart.y, currentY);
          const width = Math.abs(state.routingEditor.dragStart.x - currentX);
          const height = Math.abs(state.routingEditor.dragStart.y - currentY);
          const box = document.getElementById('routingSelectionBox');
          box.style.display = 'block';
          box.style.left = (routingCanvas.offsetLeft + left) + 'px';
          box.style.top = (routingCanvas.offsetTop + top) + 'px';
          box.style.width = width + 'px';
          box.style.height = height + 'px';
        });

        window.addEventListener('mouseup', (event) => {
          if (!state.routingEditor.dragStart) {
            return;
          }
          const bounds = routingCanvas.getBoundingClientRect();
          const endX = Math.max(0, Math.min(bounds.width, event.clientX - bounds.left));
          const endY = Math.max(0, Math.min(bounds.height, event.clientY - bounds.top));
          const left = Math.min(state.routingEditor.dragStart.x, endX);
          const top = Math.min(state.routingEditor.dragStart.y, endY);
          const width = Math.abs(state.routingEditor.dragStart.x - endX);
          const height = Math.abs(state.routingEditor.dragStart.y - endY);
          state.routingEditor.dragStart = null;
          if (width < 8 || height < 8 || !bounds.width || !bounds.height) {
            renderRoutingSelectionOverlay();
            return;
          }
          applyRoutingRectSelection({
            x: left / bounds.width,
            y: top / bounds.height,
            width: width / bounds.width,
            height: height / bounds.height
          });
        });

        bindForm('userPrinterAssignmentForm', async (data) => {
          const userId = String(data.get('userId') || '').trim();
          await api('/admin/config/user-printer-assignments/' + userId, 'PUT', {
            a4PrinterId: optional(data.get('a4PrinterId')),
            thermalPrinterId: optional(data.get('thermalPrinterId'))
          });
          await refreshAssignments();
        });

        bindForm('groupPrinterApplyForm', async (data) => {
          const groupId = String(data.get('groupId') || '').trim();
          const a4PrinterId = optional(data.get('a4PrinterId'));
          const thermalPrinterId = optional(data.get('thermalPrinterId'));
          const members = state.memberships.filter((m) => m.groupId === groupId);
          if (members.length === 0) {
            status('No users in selected group.', 'muted');
            return;
          }
          await Promise.all(
            members.map((member) =>
              api('/admin/config/user-printer-assignments/' + member.userId, 'PUT', {
                a4PrinterId,
                thermalPrinterId
              })
            )
          );
          status('Applied defaults to ' + members.length + ' users in group.', 'ok');
          await refreshAssignments();
        });

        bindForm('smbAssignForm', async (data) => {
          const sourceId = String(data.get('sourceId') || '').trim();
          await api('/admin/config/smb-sources/' + sourceId, 'PATCH', {
            ownerUserId: optional(data.get('ownerUserId')),
            ownerGroupId: optional(data.get('ownerGroupId'))
          });
          await refreshSmb();
        });

        bindForm('routingAssignForm', async (data) => {
          const routingProfileId = String(data.get('routingProfileId') || '').trim();
          await api('/admin/config/routing-profiles/' + routingProfileId, 'PATCH', {
            ownerUserId: optional(data.get('ownerUserId')),
            ownerGroupId: optional(data.get('ownerGroupId'))
          });
          await refreshRouting();
        });

        bindForm('ocrGlobalForm', async (data) => {
          const parsed = JSON.parse(String(data.get('config') || '{}'));
          await api('/admin/config/ocr/global', 'PUT', {
            provider: String(data.get('provider') || 'tesseract'),
            config: parsed
          });
          await refreshOcr();
        });

        bindForm('ocrOverrideForm', async (data) => {
          const userId = String(data.get('userId') || '').trim();
          const parsed = JSON.parse(String(data.get('config') || '{}'));
          await api('/admin/config/ocr/overrides/' + userId, 'PUT', {
            provider: optional(data.get('provider')),
            config: parsed
          });
          await refreshOcr();
        });

        bindForm('systemSettingsForm', async (data) => {
          const globalSmbDomainUsername = String(data.get('globalSmbDomainUsername') || '').trim();
          const globalPrinterDomainUsername = String(data.get('globalPrinterDomainUsername') || '').trim();
          if (globalSmbDomainUsername && !isValidDomainUsername(globalSmbDomainUsername)) {
            throw new Error('INVALID_GLOBAL_SMB_DOMAIN_USERNAME');
          }
          if (globalPrinterDomainUsername && !isValidDomainUsername(globalPrinterDomainUsername)) {
            throw new Error('INVALID_GLOBAL_PRINTER_DOMAIN_USERNAME');
          }
          await api('/admin/config/system-settings', 'PUT', {
            globalSmbDomainUsername,
            globalSmbSecretRef: String(data.get('globalSmbSecretRef') || '').trim(),
            globalPrinterDomainUsername,
            globalPrinterSecretRef: String(data.get('globalPrinterSecretRef') || '').trim(),
            workerPollIntervalMs: Number(data.get('workerPollIntervalMs') || 5000),
            smtpEnabled: Boolean(data.get('smtpEnabled')),
            smtpHost: String(data.get('smtpHost') || '').trim(),
            smtpPort: Number(data.get('smtpPort') || 25),
            smtpSecure: Boolean(data.get('smtpSecure')),
            smtpUsername: String(data.get('smtpUsername') || '').trim(),
            smtpSecretRef: String(data.get('smtpSecretRef') || '').trim(),
            smtpFrom: String(data.get('smtpFrom') || '').trim(),
            smtpTo: splitCsv(data.get('smtpTo'))
          });
          await refreshSystemSettings();
        });

        document.getElementById('runPipelineNow').addEventListener('click', async () => {
          try {
            await api('/worker/pipeline/run-once', 'POST', {});
            await refreshOperations();
            status('Worker run completed', 'ok');
          } catch (error) {
            status('Worker run failed: ' + String(error && error.message ? error.message : error), 'danger');
          }
        });

        document.getElementById('sendTestNotification').addEventListener('click', async () => {
          try {
            await api('/worker/pipeline/notifications/test', 'POST', {
              actor: state.authUser || 'admin'
            });
            await refreshOperations();
            status('SMTP test notification sent', 'ok');
          } catch (error) {
            status('SMTP test failed: ' + String(error && error.message ? error.message : error), 'danger');
            await refreshOperations().catch(() => {});
          }
        });

        document.getElementById('refreshOperations').addEventListener('click', async () => {
          try {
            await refreshOperations();
            status('Operations refreshed', 'ok');
          } catch (error) {
            status('Operations refresh failed: ' + String(error && error.message ? error.message : error), 'danger');
          }
        });

        document.getElementById('savePreferences').addEventListener('click', async () => {
          try {
            const locale = document.getElementById('localeSelect').value;
            const theme = document.getElementById('themeSelect').value;
            const result = await api('/me/preferences', 'PATCH', { locale, theme });
            await loadMessages(result.locale || locale);
            applyTheme(result.theme || theme);
            document.getElementById('prefStatus').textContent = 'Preferences saved';
            document.getElementById('prefStatus').className = 'ok';
            status('Preferences saved', 'ok');
          } catch (error) {
            document.getElementById('prefStatus').textContent = 'Unable to save preferences';
            document.getElementById('prefStatus').className = 'danger';
            status('Error: ' + String(error && error.message ? error.message : 'UNKNOWN'), 'danger');
          }
        });

        document.getElementById('localeSelect').addEventListener('change', async (event) => {
          await loadMessages(event.target.value);
        });

        document.getElementById('themeSelect').addEventListener('change', (event) => {
          applyTheme(event.target.value);
        });

        initStepNav();
        wireVisualDropzone();
        wireRoutingTestZone();
        renderAdDiscoveryLists();
        setSession(state.authToken, state.authUser);

        loadMessages(state.locale).catch(() => {});
        applyTheme(state.theme);
        loadBootstrapState().catch(() => {});

        if (state.authToken) {
          (async () => {
            try {
              const prefs = await api('/me/preferences', 'GET');
              if (prefs && prefs.locale) {
                await loadMessages(prefs.locale);
              }
              if (prefs && prefs.theme) {
                applyTheme(prefs.theme);
              }
              await refreshAll();
            } catch (error) {
              if (String(error && error.message ? error.message : '') !== 'UNAUTHORIZED') {
                status(String(error && error.message ? error.message : 'INIT_FAILED'), 'danger');
              }
            }
          })();
        } else {
          status('Sign in with an ADMIN account to manage configuration.', 'muted');
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
  const currentDir = dirname(fileURLToPath(import.meta.url));

  app.use(express.json({ limit: '15mb' }));
  app.use('/vendor/pdfjs', express.static(join(currentDir, '../node_modules/pdfjs-dist/build')));

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

  app.get('/admin/config', (_req, res) => {
    res.type('html').send(renderAdminPage());
  });

  const proxyDefinitions: ProxyDefinition[] = [
    { method: 'get', path: '/me/preferences', upstreamPath: () => '/me/preferences' },
    { method: 'patch', path: '/me/preferences', upstreamPath: () => '/me/preferences' },
    { method: 'get', path: '/me/printer-assignment', upstreamPath: () => '/me/printer-assignment' },

    { method: 'get', path: '/admin/users', upstreamPath: () => '/admin/users' },
    { method: 'post', path: '/admin/users', upstreamPath: () => '/admin/users' },
    { method: 'patch', path: '/admin/users/:userId', upstreamPath: (req) => `/admin/users/${req.params.userId}` },
    { method: 'patch', path: '/admin/users/:userId/roles', upstreamPath: (req) => `/admin/users/${req.params.userId}/roles` },
    { method: 'delete', path: '/admin/users/:userId', upstreamPath: (req) => `/admin/users/${req.params.userId}` },

    { method: 'get', path: '/admin/groups', upstreamPath: () => '/admin/groups' },
    { method: 'post', path: '/admin/groups', upstreamPath: () => '/admin/groups' },
    { method: 'patch', path: '/admin/groups/:groupId', upstreamPath: (req) => `/admin/groups/${req.params.groupId}` },
    { method: 'delete', path: '/admin/groups/:groupId', upstreamPath: (req) => `/admin/groups/${req.params.groupId}` },

    { method: 'get', path: '/admin/group-memberships', upstreamPath: () => '/admin/group-memberships' },
    { method: 'post', path: '/admin/group-memberships', upstreamPath: () => '/admin/group-memberships' },
    {
      method: 'delete',
      path: '/admin/group-memberships/:groupId/:userId',
      upstreamPath: (req) => `/admin/group-memberships/${req.params.groupId}/${req.params.userId}`
    },

    { method: 'get', path: '/admin/config/ad-sync', upstreamPath: () => '/admin/config/ad-sync' },
    { method: 'put', path: '/admin/config/ad-sync', upstreamPath: () => '/admin/config/ad-sync' },
    { method: 'post', path: '/admin/config/ad-sync/discover', upstreamPath: () => '/admin/config/ad-sync/discover' },
    { method: 'post', path: '/admin/config/ad-sync/import', upstreamPath: () => '/admin/config/ad-sync/import' },

    { method: 'get', path: '/admin/config/smb-sources', upstreamPath: () => '/admin/config/smb-sources' },
    { method: 'post', path: '/admin/config/smb-sources', upstreamPath: () => '/admin/config/smb-sources' },
    { method: 'patch', path: '/admin/config/smb-sources/:sourceId', upstreamPath: (req) => `/admin/config/smb-sources/${req.params.sourceId}` },
    { method: 'delete', path: '/admin/config/smb-sources/:sourceId', upstreamPath: (req) => `/admin/config/smb-sources/${req.params.sourceId}` },

    { method: 'get', path: '/admin/config/printers', upstreamPath: () => '/admin/config/printers' },
    { method: 'post', path: '/admin/config/printers', upstreamPath: () => '/admin/config/printers' },
    { method: 'patch', path: '/admin/config/printers/:printerId', upstreamPath: (req) => `/admin/config/printers/${req.params.printerId}` },
    { method: 'delete', path: '/admin/config/printers/:printerId', upstreamPath: (req) => `/admin/config/printers/${req.params.printerId}` },

    { method: 'get', path: '/admin/config/user-printer-assignments', upstreamPath: () => '/admin/config/user-printer-assignments' },
    { method: 'put', path: '/admin/config/user-printer-assignments/:userId', upstreamPath: (req) => `/admin/config/user-printer-assignments/${req.params.userId}` },
    {
      method: 'delete',
      path: '/admin/config/user-printer-assignments/:userId',
      upstreamPath: (req) => `/admin/config/user-printer-assignments/${req.params.userId}`
    },

    { method: 'get', path: '/admin/config/filename-masks', upstreamPath: () => '/admin/config/filename-masks' },
    { method: 'post', path: '/admin/config/filename-masks', upstreamPath: () => '/admin/config/filename-masks' },
    { method: 'patch', path: '/admin/config/filename-masks/:maskId', upstreamPath: (req) => `/admin/config/filename-masks/${req.params.maskId}` },
    { method: 'delete', path: '/admin/config/filename-masks/:maskId', upstreamPath: (req) => `/admin/config/filename-masks/${req.params.maskId}` },

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

    { method: 'get', path: '/admin/config/visual-profiles', upstreamPath: () => '/admin/config/visual-profiles' },
    { method: 'post', path: '/admin/config/visual-profiles', upstreamPath: () => '/admin/config/visual-profiles' },
    {
      method: 'patch',
      path: '/admin/config/visual-profiles/:visualProfileId',
      upstreamPath: (req) => `/admin/config/visual-profiles/${req.params.visualProfileId}`
    },
    {
      method: 'delete',
      path: '/admin/config/visual-profiles/:visualProfileId',
      upstreamPath: (req) => `/admin/config/visual-profiles/${req.params.visualProfileId}`
    },

    { method: 'get', path: '/admin/config/ocr/global', upstreamPath: () => '/admin/config/ocr/global' },
    { method: 'put', path: '/admin/config/ocr/global', upstreamPath: () => '/admin/config/ocr/global' },
    { method: 'get', path: '/admin/config/ocr/overrides', upstreamPath: () => '/admin/config/ocr/overrides' },
    { method: 'put', path: '/admin/config/ocr/overrides/:userId', upstreamPath: (req) => `/admin/config/ocr/overrides/${req.params.userId}` },
    { method: 'delete', path: '/admin/config/ocr/overrides/:userId', upstreamPath: (req) => `/admin/config/ocr/overrides/${req.params.userId}` },
    { method: 'get', path: '/admin/config/system-settings', upstreamPath: () => '/admin/config/system-settings' },
    { method: 'put', path: '/admin/config/system-settings', upstreamPath: () => '/admin/config/system-settings' },
    { method: 'get', path: '/admin/logs', upstreamPath: () => '/admin/logs' }
  ];

  for (const definition of proxyDefinitions) {
    registerProxyRoute(app, { fetchImpl, apiBaseUrl }, definition);
  }

  const workerProxyDefinitions: ProxyDefinition[] = [
    { method: 'get', path: '/worker/pipeline/status', upstreamPath: () => '/pipeline/status' },
    { method: 'post', path: '/worker/pipeline/run-once', upstreamPath: () => '/pipeline/run-once' },
    { method: 'get', path: '/worker/pipeline/notifications', upstreamPath: () => '/pipeline/notifications' },
    { method: 'post', path: '/worker/pipeline/notifications/test', upstreamPath: () => '/pipeline/notifications/test' },
    { method: 'get', path: '/worker/pipeline/jobs', upstreamPath: () => '/pipeline/jobs' },
    { method: 'get', path: '/worker/pipeline/jobs/:jobId/pages', upstreamPath: (req) => `/pipeline/jobs/${req.params.jobId}/pages` },
    { method: 'post', path: '/worker/pipeline/jobs/:jobId/cancel', upstreamPath: (req) => `/pipeline/jobs/${req.params.jobId}/cancel` },
    { method: 'post', path: '/worker/pipeline/jobs/:jobId/retry', upstreamPath: (req) => `/pipeline/jobs/${req.params.jobId}/retry` }
  ];

  for (const definition of workerProxyDefinitions) {
    registerProxyRoute(app, { fetchImpl, apiBaseUrl: workerBaseUrl }, definition);
  }

  return app;
}
