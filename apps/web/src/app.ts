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
        --bg: #f6f7fb;
        --card: #ffffff;
        --text: #0e172a;
        --muted: #475569;
        --border: #d7deea;
      }

      :root[data-theme='dark'] {
        --bg: #0b1220;
        --card: #111a2c;
        --text: #e2e8f0;
        --muted: #94a3b8;
        --border: #233048;
      }

      body {
        margin: 0;
        padding: 1.5rem;
        background: var(--bg);
        color: var(--text);
        font-family: Inter, system-ui, -apple-system, sans-serif;
      }

      h1, h2 {
        margin-top: 0;
      }

      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
        gap: 1rem;
      }

      section, .panel {
        background: var(--card);
        border: 1px solid var(--border);
        border-radius: 0.75rem;
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
        background: transparent;
        color: inherit;
      }

      textarea {
        min-height: 100px;
      }

      button {
        cursor: pointer;
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
    </style>
  </head>
  <body>
    <h1 data-i18n="nav.adminConfig">Admin configuration</h1>

    <div class="panel">
      <label data-i18n="token.label" for="authToken">Auth token</label>
      <input id="authToken" type="password" placeholder="paste JWT token" />
      <div class="muted" data-i18n="token.help">Use ADMIN token for config actions</div>
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
        var tokenInput = document.getElementById('authToken');
        var localeSelect = document.getElementById('localeSelect');
        var themeSelect = document.getElementById('themeSelect');
        var prefStatus = document.getElementById('prefStatus');
        var ocrGlobalPreview = document.getElementById('ocrGlobalPreview');

        var state = {
          locale: 'en-US',
          theme: 'system',
          messages: {}
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
          var token = tokenInput.value.trim();
          var headers = {};
          if (token) {
            headers['x-auth-token'] = token;
          }
          if (includeJsonContentType) {
            headers['content-type'] = 'application/json';
          }
          return headers;
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
          if (contentType.indexOf('application/json') >= 0) {
            return response.json();
          }
          return response.text();
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

        tokenInput.addEventListener('change', async function () {
          await loadPreferences();
          await Promise.all([refreshSmb(), refreshPrinters(), refreshMasks(), refreshRouting(), refreshOcrGlobal(), refreshOcrOverrides()]);
        });

        loadPreferences().then(function () {
          return Promise.all([refreshSmb(), refreshPrinters(), refreshMasks(), refreshRouting(), refreshOcrGlobal(), refreshOcrOverrides()]);
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

  app.get('/admin/config', (_req, res) => {
    res.type('html').send(renderAdminConfigPage());
  });

  const proxyDefinitions: ProxyDefinition[] = [
    { method: 'get', path: '/me/preferences', upstreamPath: () => '/me/preferences' },
    { method: 'patch', path: '/me/preferences', upstreamPath: () => '/me/preferences' },

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
