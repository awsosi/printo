import express, { type Request, type Response } from 'express';
import { getDefaultLocale, resolveMessages } from './i18n.js';

type FetchLike = typeof fetch;

type HttpMethod = 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE';
type ExpressMethod = 'get' | 'post' | 'patch' | 'put' | 'delete';

interface CreateWebAppOptions {
  fetchImpl?: FetchLike;
  apiBaseUrl?: string;
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
    const response = await fetchImpl(`${apiBaseUrl}${upstreamPath}`, requestInit);
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

function renderAdminConfigPage() {
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <title>printo admin config</title>
    <style>
      :root {
        color-scheme: light dark;
        --bg: radial-gradient(circle at 20% 20%, #fef3c7 0%, #e0f2fe 45%, #f8fafc 100%);
        --card: rgba(255, 255, 255, 0.82);
        --text: #102a43;
        --muted: #486581;
        --border: #bcccdc;
        --accent: #1d4ed8;
        --accent-strong: #1e3a8a;
      }

      :root[data-theme='dark'] {
        --bg: radial-gradient(circle at 20% 20%, #3f6212 0%, #0f172a 45%, #020617 100%);
        --card: rgba(15, 23, 42, 0.88);
        --text: #e2e8f0;
        --muted: #94a3b8;
        --border: #334155;
        --accent: #60a5fa;
        --accent-strong: #93c5fd;
      }

      body {
        margin: 0;
        padding: 1.5rem;
        background: var(--bg);
        color: var(--text);
        font-family: "Space Grotesk", "Avenir Next", "Segoe UI", sans-serif;
      }

      h1, h2 {
        margin-top: 0;
      }

      .hero {
        display: flex;
        justify-content: space-between;
        align-items: flex-end;
        gap: 1rem;
        margin-bottom: 1rem;
      }

      .hero h1 {
        margin-bottom: 0.2rem;
        letter-spacing: 0.01em;
      }

      .hero p {
        margin: 0;
        color: var(--muted);
      }

      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
        gap: 1rem;
      }

      section, .panel {
        background: var(--card);
        border: 1px solid var(--border);
        border-radius: 0.9rem;
        backdrop-filter: blur(3px);
        padding: 1rem;
      }

      label {
        display: block;
        margin-bottom: 0.5rem;
        color: var(--muted);
        font-size: 0.92rem;
      }

      input, textarea, select, button {
        width: 100%;
        box-sizing: border-box;
        border-radius: 0.5rem;
        border: 1px solid var(--border);
        padding: 0.55rem 0.65rem;
        margin-bottom: 0.55rem;
        background: rgba(255, 255, 255, 0.3);
        color: inherit;
      }

      textarea {
        min-height: 100px;
      }

      button {
        cursor: pointer;
        font-weight: 600;
        background: var(--accent);
        border-color: transparent;
        color: #fff;
      }

      button:hover {
        background: var(--accent-strong);
      }

      button.secondary {
        background: transparent;
        border: 1px solid var(--border);
        color: inherit;
      }

      .inline-actions {
        display: flex;
        gap: 0.5rem;
      }

      .inline-actions button {
        width: auto;
      }

      ul {
        margin: 0.5rem 0 0;
        padding-left: 1rem;
      }

      li {
        margin-bottom: 0.5rem;
      }

      .muted {
        color: var(--muted);
      }

      .status {
        min-height: 1.25rem;
      }

      .row {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 0.5rem;
      }

      .auth-grid {
        display: grid;
        grid-template-columns: 1fr auto;
        gap: 0.5rem;
      }

      .status-chip {
        display: inline-flex;
        align-items: center;
        padding: 0.25rem 0.55rem;
        border-radius: 99px;
        border: 1px solid var(--border);
        color: var(--muted);
        font-size: 0.83rem;
      }

      .danger {
        color: #ef4444;
      }

      @media (max-width: 768px) {
        .hero {
          flex-direction: column;
          align-items: flex-start;
        }

        .row {
          grid-template-columns: 1fr;
        }

        .auth-grid {
          grid-template-columns: 1fr;
        }
      }
    </style>
  </head>
  <body>
    <header class="hero">
      <div>
        <h1 data-i18n="nav.adminConfig">Admin configuration</h1>
        <p>Login first, then manage routing and printer operations from one dashboard.</p>
      </div>
      <div id="authSessionBadge" class="status-chip">Signed out</div>
    </header>

    <div class="panel">
      <h2>Admin login</h2>
      <form id="loginForm">
        <div class="auth-grid">
          <input id="loginUsername" name="username" placeholder="username" autocomplete="username" required />
          <input id="loginPassword" name="password" type="password" placeholder="password" autocomplete="current-password" required />
        </div>
        <div class="inline-actions">
          <button id="loginButton" type="submit">Sign in</button>
          <button id="logoutButton" type="button" class="secondary">Sign out</button>
        </div>
      </form>
      <div id="authStatus" class="status muted"></div>
    </div>

    <div class="panel">
      <h2 data-i18n="settings.title">User preferences</h2>
      <div class="row">
        <div>
          <label data-i18n="settings.locale" for="localeSelect">Language</label>
          <select id="localeSelect">
            <option value="en-US">English (en-US)</option>
            <option value="pl-PL">Polski (pl-PL)</option>
          </select>
        </div>
        <div>
          <label data-i18n="settings.theme" for="themeSelect">Theme</label>
          <select id="themeSelect">
            <option value="system" data-i18n="theme.system">System</option>
            <option value="light" data-i18n="theme.light">Light</option>
            <option value="dark" data-i18n="theme.dark">Dark</option>
          </select>
        </div>
      </div>
      <button id="savePreferences" type="button" data-i18n="settings.save">Save preferences</button>
      <div id="prefStatus" class="status muted"></div>
    </div>

    <div class="grid">
      <section>
        <h2 data-i18n="section.smb">SMB sources</h2>
        <button id="smbRefresh" type="button" data-i18n="button.refresh">Refresh</button>
        <ul id="smbList"></ul>
        <form id="smbCreateForm">
          <label data-i18n="smb.path">SMB path</label>
          <input name="path" required />
          <label data-i18n="smb.domainUsername">Domain username</label>
          <input name="domainUsername" required />
          <label data-i18n="smb.secretRef">Secret reference</label>
          <input name="secretRef" required />
          <button type="submit" data-i18n="button.create">Create</button>
        </form>
      </section>

      <section>
        <h2 data-i18n="section.printers">Printers</h2>
        <button id="printerRefresh" type="button" data-i18n="button.refresh">Refresh</button>
        <ul id="printerList"></ul>
        <form id="printerCreateForm">
          <label data-i18n="printers.name">Printer name</label>
          <input name="name" required />
          <label data-i18n="printers.type">Printer type</label>
          <select name="type">
            <option value="A4">A4</option>
            <option value="THERMAL">THERMAL</option>
          </select>
          <label data-i18n="printers.targetUri">Target URI</label>
          <input name="targetUri" required />
          <button type="submit" data-i18n="button.create">Create</button>
        </form>
      </section>

      <section>
        <h2 data-i18n="section.userPrinterAssignments">User printer assignments</h2>
        <button id="userPrinterAssignmentRefresh" type="button" data-i18n="button.refresh">Refresh</button>
        <ul id="userPrinterAssignmentList"></ul>
        <form id="userPrinterAssignmentForm">
          <label data-i18n="assignments.userId">User ID</label>
          <input name="userId" required />
          <label data-i18n="assignments.a4PrinterId">A4 printer ID</label>
          <input name="a4PrinterId" />
          <label data-i18n="assignments.thermalPrinterId">Thermal printer ID</label>
          <input name="thermalPrinterId" />
          <button type="submit" data-i18n="settings.save">Save preferences</button>
        </form>
      </section>

      <section>
        <h2 data-i18n="section.masks">Filename masks</h2>
        <button id="maskRefresh" type="button" data-i18n="button.refresh">Refresh</button>
        <ul id="maskList"></ul>
        <form id="maskCreateForm">
          <label data-i18n="masks.pattern">Filename pattern</label>
          <input name="pattern" required />
          <label>
            <input name="isRegex" type="checkbox" />
            <span data-i18n="masks.regex">Regex</span>
          </label>
          <button type="submit" data-i18n="button.create">Create</button>
        </form>
      </section>

      <section>
        <h2 data-i18n="section.routing">Routing profiles</h2>
        <button id="routingRefresh" type="button" data-i18n="button.refresh">Refresh</button>
        <ul id="routingList"></ul>
        <form id="routingCreateForm">
          <label data-i18n="routing.name">Profile name</label>
          <input name="name" required />
          <label data-i18n="routing.patterns">Thermal label patterns (comma-separated)</label>
          <input name="patterns" />
          <label data-i18n="routing.fallbackPrinterId">Fallback printer ID</label>
          <input name="fallbackPrinterId" />
          <button type="submit" data-i18n="button.create">Create</button>
        </form>
      </section>

      <section>
        <h2 data-i18n="section.ocr">OCR configuration</h2>
        <div class="inline-actions">
          <button id="ocrGlobalRefresh" type="button" data-i18n="button.refresh">Refresh</button>
          <button id="ocrOverrideRefresh" type="button" data-i18n="button.refresh">Refresh</button>
        </div>
        <pre id="ocrGlobalPreview" class="muted"></pre>
        <form id="ocrGlobalForm">
          <label data-i18n="ocr.provider">OCR provider</label>
          <input name="provider" required />
          <label data-i18n="ocr.config">OCR config JSON</label>
          <textarea name="config">{}</textarea>
          <button type="submit" data-i18n="settings.save">Save preferences</button>
        </form>
        <ul id="ocrOverrideList"></ul>
        <form id="ocrOverrideForm">
          <label data-i18n="ocr.userId">User ID</label>
          <input name="userId" required />
          <label data-i18n="ocr.overrideProvider">Override provider</label>
          <input name="provider" />
          <label data-i18n="ocr.overrideConfig">Override config JSON</label>
          <textarea name="config">{}</textarea>
          <button type="submit" data-i18n="button.create">Create</button>
        </form>
      </section>
    </div>

    <script>
      (function () {
        var loginForm = document.getElementById('loginForm');
        var loginUsername = document.getElementById('loginUsername');
        var loginPassword = document.getElementById('loginPassword');
        var logoutButton = document.getElementById('logoutButton');
        var authStatus = document.getElementById('authStatus');
        var authSessionBadge = document.getElementById('authSessionBadge');
        var localeSelect = document.getElementById('localeSelect');
        var themeSelect = document.getElementById('themeSelect');
        var prefStatus = document.getElementById('prefStatus');
        var ocrGlobalPreview = document.getElementById('ocrGlobalPreview');

        var state = {
          locale: 'en-US',
          theme: 'system',
          messages: {},
          authToken: '',
          authUser: ''
        };

        var systemThemeQuery = window.matchMedia('(prefers-color-scheme: dark)');

        function applyTheme(themeMode) {
          state.theme = themeMode;
          var resolvedTheme = themeMode;
          if (themeMode === 'system') {
            resolvedTheme = systemThemeQuery.matches ? 'dark' : 'light';
          }
          document.documentElement.setAttribute('data-theme', resolvedTheme);
        }

        systemThemeQuery.addEventListener('change', function () {
          if (state.theme === 'system') {
            applyTheme('system');
          }
        });

        function tokenHeaders(includeJsonContentType) {
          var token = state.authToken;
          var headers = {};
          if (token) {
            headers['x-auth-token'] = token;
          }
          if (includeJsonContentType) {
            headers['content-type'] = 'application/json';
          }
          return headers;
        }

        function updateAuthUi() {
          var isAuthed = Boolean(state.authToken);
          if (isAuthed) {
            authSessionBadge.textContent = 'Signed in as ' + (state.authUser || 'user');
            authStatus.textContent = '';
            authStatus.classList.remove('danger');
          } else {
            authSessionBadge.textContent = 'Signed out';
          }
        }

        function setAuthSession(token, username) {
          state.authToken = token || '';
          state.authUser = username || '';
          if (state.authToken) {
            localStorage.setItem('printo_auth_token', state.authToken);
            localStorage.setItem('printo_auth_user', state.authUser);
          } else {
            localStorage.removeItem('printo_auth_token');
            localStorage.removeItem('printo_auth_user');
          }
          updateAuthUi();
        }

        function parseJsonSafe(inputText) {
          try {
            return JSON.parse(inputText || '{}');
          } catch {
            return null;
          }
        }

        function splitPatterns(inputText) {
          if (!inputText) {
            return [];
          }
          return inputText
            .split(',')
            .map(function (value) {
              return value.trim();
            })
            .filter(Boolean);
        }

        async function apiRequest(path, method, body) {
          var requestOptions = {
            method: method || 'GET',
            headers: tokenHeaders(Boolean(body))
          };

          if (body) {
            requestOptions.body = JSON.stringify(body);
          }

          var response = await fetch(path, requestOptions);
          if (response.status === 204) {
            return null;
          }
          var contentType = response.headers.get('content-type') || '';
          var payload = contentType.indexOf('application/json') >= 0 ? await response.json() : await response.text();
          if (!response.ok) {
            var errorMessage = typeof payload === 'string' ? payload : payload && payload.error ? payload.error : 'REQUEST_FAILED';
            throw new Error(errorMessage);
          }
          return payload;
        }

        async function loadMessages(locale) {
          var response = await fetch('/i18n/messages?locale=' + encodeURIComponent(locale));
          var payload = await response.json();
          state.locale = payload.locale;
          state.messages = payload.messages;
          localeSelect.value = payload.locale;
          translateText();
        }

        function translateText() {
          var nodes = document.querySelectorAll('[data-i18n]');
          nodes.forEach(function (node) {
            var key = node.getAttribute('data-i18n');
            if (!key) {
              return;
            }
            var translated = state.messages[key] || key;
            node.textContent = translated;
          });
        }

        async function loadPreferences() {
          try {
            var preferences = await apiRequest('/me/preferences', 'GET');
            if (preferences && preferences.locale) {
              state.locale = preferences.locale;
            }
            if (preferences && preferences.theme) {
              state.theme = preferences.theme;
            }
          } catch {
            state.locale = navigator.language || 'en-US';
            state.theme = 'system';
          }

          await loadMessages(state.locale);
          themeSelect.value = state.theme;
          applyTheme(state.theme);
        }

        async function savePreferences() {
          prefStatus.textContent = '';
          try {
            var result = await apiRequest('/me/preferences', 'PATCH', {
              locale: localeSelect.value,
              theme: themeSelect.value
            });
            state.locale = result.locale;
            state.theme = result.theme;
            await loadMessages(state.locale);
            applyTheme(state.theme);
            prefStatus.textContent = state.messages['settings.status.saved'] || 'Preferences saved';
          } catch {
            prefStatus.textContent = state.messages['settings.status.error'] || 'Unable to save preferences';
          }
        }

        async function loginWithCredentials(username, password) {
          authStatus.textContent = '';
          authStatus.classList.remove('danger');
          try {
            var response = await fetch('/auth/login', {
              method: 'POST',
              headers: { 'content-type': 'application/json' },
              body: JSON.stringify({ username: username, password: password })
            });

            var loginPayload = await response.json();
            if (!response.ok || !loginPayload.accessToken) {
              throw new Error(loginPayload.error || 'INVALID_LOGIN');
            }

            setAuthSession(loginPayload.accessToken, loginPayload.user && loginPayload.user.username ? loginPayload.user.username : username);
            authStatus.textContent = 'Login successful.';
            await bootstrapData();
          } catch (error) {
            authStatus.textContent = 'Login failed: ' + String(error && error.message ? error.message : 'UNKNOWN');
            authStatus.classList.add('danger');
          }
        }

        async function bootstrapData() {
          if (!state.authToken) {
            return;
          }

          await loadPreferences();
          await Promise.all([
            refreshSmb(),
            refreshPrinters(),
            refreshUserPrinterAssignments(),
            refreshMasks(),
            refreshRouting(),
            refreshOcrGlobal(),
            refreshOcrOverrides()
          ]);
        }

        function renderList(targetId, items, getDeletePath, getDeleteId) {
          var list = document.getElementById(targetId);
          list.innerHTML = '';

          if (!Array.isArray(items) || items.length === 0) {
            var empty = document.createElement('li');
            empty.className = 'muted';
            empty.textContent = '[]';
            list.appendChild(empty);
            return;
          }

          items.forEach(function (entry) {
            var li = document.createElement('li');
            var text = document.createElement('code');
            text.textContent = JSON.stringify(entry);

            var button = document.createElement('button');
            button.type = 'button';
            button.textContent = state.messages['button.delete'] || 'Delete';
            button.style.marginTop = '0.35rem';
            button.addEventListener('click', async function () {
              var deleteId = getDeleteId(entry);
              await apiRequest(getDeletePath(deleteId), 'DELETE');
              button.dispatchEvent(new Event('deleted'));
            });

            li.appendChild(text);
            li.appendChild(document.createElement('br'));
            li.appendChild(button);
            list.appendChild(li);
          });
        }

        async function refreshSmb() {
          var records = await apiRequest('/admin/config/smb-sources', 'GET');
          renderList('smbList', records, function (id) {
            return '/admin/config/smb-sources/' + id;
          }, function (entry) {
            return entry.id;
          });

          document.querySelectorAll('#smbList button').forEach(function (button) {
            button.addEventListener('deleted', refreshSmb);
          });
        }

        async function refreshPrinters() {
          var records = await apiRequest('/admin/config/printers', 'GET');
          renderList('printerList', records, function (id) {
            return '/admin/config/printers/' + id;
          }, function (entry) {
            return entry.id;
          });

          document.querySelectorAll('#printerList button').forEach(function (button) {
            button.addEventListener('deleted', refreshPrinters);
          });
        }

        async function refreshUserPrinterAssignments() {
          var records = await apiRequest('/admin/config/user-printer-assignments', 'GET');
          renderList('userPrinterAssignmentList', records, function (id) {
            return '/admin/config/user-printer-assignments/' + id;
          }, function (entry) {
            return entry.userId;
          });

          document.querySelectorAll('#userPrinterAssignmentList button').forEach(function (button) {
            button.addEventListener('deleted', refreshUserPrinterAssignments);
          });
        }

        async function refreshMasks() {
          var records = await apiRequest('/admin/config/filename-masks', 'GET');
          renderList('maskList', records, function (id) {
            return '/admin/config/filename-masks/' + id;
          }, function (entry) {
            return entry.id;
          });

          document.querySelectorAll('#maskList button').forEach(function (button) {
            button.addEventListener('deleted', refreshMasks);
          });
        }

        async function refreshRouting() {
          var records = await apiRequest('/admin/config/routing-profiles', 'GET');
          renderList('routingList', records, function (id) {
            return '/admin/config/routing-profiles/' + id;
          }, function (entry) {
            return entry.id;
          });

          document.querySelectorAll('#routingList button').forEach(function (button) {
            button.addEventListener('deleted', refreshRouting);
          });
        }

        async function refreshOcrGlobal() {
          var record = await apiRequest('/admin/config/ocr/global', 'GET');
          ocrGlobalPreview.textContent = JSON.stringify(record, null, 2);
        }

        async function refreshOcrOverrides() {
          var records = await apiRequest('/admin/config/ocr/overrides', 'GET');
          renderList('ocrOverrideList', records, function (id) {
            return '/admin/config/ocr/overrides/' + id;
          }, function (entry) {
            return entry.userId;
          });

          document.querySelectorAll('#ocrOverrideList button').forEach(function (button) {
            button.addEventListener('deleted', refreshOcrOverrides);
          });
        }

        function bindForm(formId, submitHandler) {
          var form = document.getElementById(formId);
          form.addEventListener('submit', async function (event) {
            event.preventDefault();
            await submitHandler(new FormData(form));
            form.reset();
          });
        }

        document.getElementById('smbRefresh').addEventListener('click', refreshSmb);
        document.getElementById('printerRefresh').addEventListener('click', refreshPrinters);
        document.getElementById('userPrinterAssignmentRefresh').addEventListener('click', refreshUserPrinterAssignments);
        document.getElementById('maskRefresh').addEventListener('click', refreshMasks);
        document.getElementById('routingRefresh').addEventListener('click', refreshRouting);
        document.getElementById('ocrGlobalRefresh').addEventListener('click', refreshOcrGlobal);
        document.getElementById('ocrOverrideRefresh').addEventListener('click', refreshOcrOverrides);
        document.getElementById('savePreferences').addEventListener('click', savePreferences);

        bindForm('smbCreateForm', async function (formData) {
          await apiRequest('/admin/config/smb-sources', 'POST', {
            path: String(formData.get('path') || ''),
            domainUsername: String(formData.get('domainUsername') || ''),
            secretRef: String(formData.get('secretRef') || '')
          });
          await refreshSmb();
        });

        bindForm('printerCreateForm', async function (formData) {
          await apiRequest('/admin/config/printers', 'POST', {
            name: String(formData.get('name') || ''),
            type: String(formData.get('type') || 'A4'),
            targetUri: String(formData.get('targetUri') || '')
          });
          await refreshPrinters();
        });

        bindForm('userPrinterAssignmentForm', async function (formData) {
          var userId = String(formData.get('userId') || '').trim();
          var a4PrinterId = String(formData.get('a4PrinterId') || '').trim();
          var thermalPrinterId = String(formData.get('thermalPrinterId') || '').trim();

          await apiRequest('/admin/config/user-printer-assignments/' + userId, 'PUT', {
            a4PrinterId: a4PrinterId || null,
            thermalPrinterId: thermalPrinterId || null
          });

          await refreshUserPrinterAssignments();
        });

        bindForm('maskCreateForm', async function (formData) {
          await apiRequest('/admin/config/filename-masks', 'POST', {
            pattern: String(formData.get('pattern') || ''),
            isRegex: Boolean(formData.get('isRegex'))
          });
          await refreshMasks();
        });

        bindForm('routingCreateForm', async function (formData) {
          var fallbackPrinterId = String(formData.get('fallbackPrinterId') || '').trim();
          await apiRequest('/admin/config/routing-profiles', 'POST', {
            name: String(formData.get('name') || ''),
            thermalLabelPatterns: splitPatterns(String(formData.get('patterns') || '')),
            fallbackPrinterId: fallbackPrinterId || null
          });
          await refreshRouting();
        });

        bindForm('ocrGlobalForm', async function (formData) {
          var parsedConfig = parseJsonSafe(String(formData.get('config') || '{}'));
          if (!parsedConfig) {
            return;
          }
          await apiRequest('/admin/config/ocr/global', 'PUT', {
            provider: String(formData.get('provider') || ''),
            config: parsedConfig
          });
          await refreshOcrGlobal();
        });

        bindForm('ocrOverrideForm', async function (formData) {
          var parsedConfig = parseJsonSafe(String(formData.get('config') || '{}'));
          if (!parsedConfig) {
            return;
          }
          var userId = String(formData.get('userId') || '').trim();
          var provider = String(formData.get('provider') || '').trim();
          await apiRequest('/admin/config/ocr/overrides/' + userId, 'PUT', {
            provider: provider || null,
            config: parsedConfig
          });
          await refreshOcrOverrides();
        });

        loginForm.addEventListener('submit', async function (event) {
          event.preventDefault();
          await loginWithCredentials(loginUsername.value.trim(), loginPassword.value);
          loginPassword.value = '';
        });

        logoutButton.addEventListener('click', function () {
          setAuthSession('', '');
          authStatus.textContent = 'Signed out.';
          authStatus.classList.remove('danger');
        });

        var storedToken = localStorage.getItem('printo_auth_token') || '';
        var storedUser = localStorage.getItem('printo_auth_user') || '';
        setAuthSession(storedToken, storedUser);

        loadMessages(navigator.language || 'en-US').then(function () {
          applyTheme('system');
          if (!state.authToken) {
            authStatus.textContent = 'Sign in with an ADMIN account to load config.';
            return null;
          }
          return bootstrapData();
        });
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

  app.use(express.json());

  app.get('/health', (_req, res) => {
    res.json({ service: 'web', status: 'ok' });
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

  app.post('/auth/login', async (req, res) => {
    try {
      const response = await fetchImpl(`${apiBaseUrl}/auth/login`, {
        method: 'POST',
        headers: {
          accept: 'application/json',
          'content-type': 'application/json'
        },
        body: JSON.stringify(req.body ?? {})
      });

      const contentType = response.headers.get('content-type') ?? '';
      if (contentType.includes('application/json')) {
        return res.status(response.status).json(await response.json());
      }

      return res.status(response.status).send(await response.text());
    } catch {
      return res.status(502).json({ error: 'UPSTREAM_UNAVAILABLE' });
    }
  });

  app.get('/admin/config', (_req, res) => {
    res.type('html').send(renderAdminConfigPage());
  });

  const proxyDefinitions: ProxyDefinition[] = [
    { method: 'get', path: '/me/preferences', upstreamPath: () => '/me/preferences' },
    { method: 'patch', path: '/me/preferences', upstreamPath: () => '/me/preferences' },
    { method: 'get', path: '/me/printer-assignment', upstreamPath: () => '/me/printer-assignment' },

    { method: 'get', path: '/admin/config/smb-sources', upstreamPath: () => '/admin/config/smb-sources' },
    { method: 'post', path: '/admin/config/smb-sources', upstreamPath: () => '/admin/config/smb-sources' },
    {
      method: 'patch',
      path: '/admin/config/smb-sources/:sourceId',
      upstreamPath: (req) => `/admin/config/smb-sources/${req.params.sourceId}`
    },
    {
      method: 'delete',
      path: '/admin/config/smb-sources/:sourceId',
      upstreamPath: (req) => `/admin/config/smb-sources/${req.params.sourceId}`
    },

    { method: 'get', path: '/admin/config/printers', upstreamPath: () => '/admin/config/printers' },
    { method: 'post', path: '/admin/config/printers', upstreamPath: () => '/admin/config/printers' },
    {
      method: 'patch',
      path: '/admin/config/printers/:printerId',
      upstreamPath: (req) => `/admin/config/printers/${req.params.printerId}`
    },
    {
      method: 'delete',
      path: '/admin/config/printers/:printerId',
      upstreamPath: (req) => `/admin/config/printers/${req.params.printerId}`
    },

    { method: 'get', path: '/admin/config/user-printer-assignments', upstreamPath: () => '/admin/config/user-printer-assignments' },
    {
      method: 'put',
      path: '/admin/config/user-printer-assignments/:userId',
      upstreamPath: (req) => `/admin/config/user-printer-assignments/${req.params.userId}`
    },
    {
      method: 'delete',
      path: '/admin/config/user-printer-assignments/:userId',
      upstreamPath: (req) => `/admin/config/user-printer-assignments/${req.params.userId}`
    },

    { method: 'get', path: '/admin/config/filename-masks', upstreamPath: () => '/admin/config/filename-masks' },
    { method: 'post', path: '/admin/config/filename-masks', upstreamPath: () => '/admin/config/filename-masks' },
    {
      method: 'patch',
      path: '/admin/config/filename-masks/:maskId',
      upstreamPath: (req) => `/admin/config/filename-masks/${req.params.maskId}`
    },
    {
      method: 'delete',
      path: '/admin/config/filename-masks/:maskId',
      upstreamPath: (req) => `/admin/config/filename-masks/${req.params.maskId}`
    },

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

    { method: 'get', path: '/admin/config/ocr/global', upstreamPath: () => '/admin/config/ocr/global' },
    { method: 'put', path: '/admin/config/ocr/global', upstreamPath: () => '/admin/config/ocr/global' },

    { method: 'get', path: '/admin/config/ocr/overrides', upstreamPath: () => '/admin/config/ocr/overrides' },
    {
      method: 'put',
      path: '/admin/config/ocr/overrides/:userId',
      upstreamPath: (req) => `/admin/config/ocr/overrides/${req.params.userId}`
    },
    {
      method: 'delete',
      path: '/admin/config/ocr/overrides/:userId',
      upstreamPath: (req) => `/admin/config/ocr/overrides/${req.params.userId}`
    }
  ];

  for (const definition of proxyDefinitions) {
    registerProxyRoute(app, { fetchImpl, apiBaseUrl }, definition);
  }

  return app;
}
