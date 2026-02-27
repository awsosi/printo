import express, { type Request, type Response } from 'express';

type FetchLike = typeof fetch;

interface CreateWebAppOptions {
  fetchImpl?: FetchLike;
  apiBaseUrl?: string;
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
  method: 'GET' | 'POST' | 'PATCH' | 'DELETE';
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

  if (method === 'POST' || method === 'PATCH') {
    headers['content-type'] = 'application/json';
    requestInit.body = JSON.stringify(req.body ?? {});
  }

  try {
    const response = await fetchImpl(`${apiBaseUrl}${upstreamPath}`, requestInit);
    const contentType = response.headers.get('content-type') ?? '';

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
  </head>
  <body>
    <h1>printo admin config</h1>
    <label>
      Auth token
      <input id="authToken" type="password" placeholder="paste JWT token" />
    </label>

    <section>
      <h2>SMB sources</h2>
      <button id="smbRefresh" type="button">Refresh SMB sources</button>
      <ul id="smbList"></ul>
      <form id="smbCreateForm">
        <input name="path" placeholder="\\\\server\\share" required />
        <input name="domainUsername" placeholder="DOMAIN\\service" required />
        <input name="secretRef" placeholder="vault://secret/ref" required />
        <button type="submit">Create SMB source</button>
      </form>
    </section>

    <section>
      <h2>Printers</h2>
      <button id="printerRefresh" type="button">Refresh printers</button>
      <ul id="printerList"></ul>
      <form id="printerCreateForm">
        <input name="name" placeholder="Printer name" required />
        <select name="type">
          <option value="A4">A4</option>
          <option value="THERMAL">THERMAL</option>
        </select>
        <input name="targetUri" placeholder="ipp://printer.local/queue" required />
        <button type="submit">Create printer</button>
      </form>
    </section>

    <script>
      const tokenInput = document.getElementById('authToken');
      const smbList = document.getElementById('smbList');
      const printerList = document.getElementById('printerList');

      function authHeaders() {
        const token = tokenInput.value.trim();
        return token ? { 'x-auth-token': token } : {};
      }

      async function loadList(path, target) {
        const response = await fetch(path, { headers: authHeaders() });
        const data = await response.json();
        target.innerHTML = '';
        if (!Array.isArray(data)) {
          const li = document.createElement('li');
          li.textContent = JSON.stringify(data);
          target.appendChild(li);
          return;
        }
        data.forEach((entry) => {
          const li = document.createElement('li');
          li.textContent = JSON.stringify(entry);
          target.appendChild(li);
        });
      }

      document.getElementById('smbRefresh').addEventListener('click', () => loadList('/admin/config/smb-sources', smbList));
      document.getElementById('printerRefresh').addEventListener('click', () => loadList('/admin/config/printers', printerList));

      document.getElementById('smbCreateForm').addEventListener('submit', async (event) => {
        event.preventDefault();
        const formData = new FormData(event.target);
        await fetch('/admin/config/smb-sources', {
          method: 'POST',
          headers: { ...authHeaders(), 'content-type': 'application/json' },
          body: JSON.stringify({
            path: String(formData.get('path')),
            domainUsername: String(formData.get('domainUsername')),
            secretRef: String(formData.get('secretRef'))
          })
        });
        await loadList('/admin/config/smb-sources', smbList);
      });

      document.getElementById('printerCreateForm').addEventListener('submit', async (event) => {
        event.preventDefault();
        const formData = new FormData(event.target);
        await fetch('/admin/config/printers', {
          method: 'POST',
          headers: { ...authHeaders(), 'content-type': 'application/json' },
          body: JSON.stringify({
            name: String(formData.get('name')),
            type: String(formData.get('type')),
            targetUri: String(formData.get('targetUri'))
          })
        });
        await loadList('/admin/config/printers', printerList);
      });
    </script>
  </body>
</html>`;
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
    res.type('html').send('<a href="/admin/config">Open admin config</a>');
  });

  app.get('/admin/config', (_req, res) => {
    res.type('html').send(renderAdminConfigPage());
  });

  app.get('/admin/config/smb-sources', async (req, res) => {
    return sendProxyRequest({
      req,
      res,
      fetchImpl,
      apiBaseUrl,
      upstreamPath: '/admin/config/smb-sources',
      method: 'GET'
    });
  });

  app.post('/admin/config/smb-sources', async (req, res) => {
    return sendProxyRequest({
      req,
      res,
      fetchImpl,
      apiBaseUrl,
      upstreamPath: '/admin/config/smb-sources',
      method: 'POST'
    });
  });

  app.patch('/admin/config/smb-sources/:sourceId', async (req, res) => {
    return sendProxyRequest({
      req,
      res,
      fetchImpl,
      apiBaseUrl,
      upstreamPath: `/admin/config/smb-sources/${req.params.sourceId}`,
      method: 'PATCH'
    });
  });

  app.delete('/admin/config/smb-sources/:sourceId', async (req, res) => {
    return sendProxyRequest({
      req,
      res,
      fetchImpl,
      apiBaseUrl,
      upstreamPath: `/admin/config/smb-sources/${req.params.sourceId}`,
      method: 'DELETE'
    });
  });

  app.get('/admin/config/printers', async (req, res) => {
    return sendProxyRequest({
      req,
      res,
      fetchImpl,
      apiBaseUrl,
      upstreamPath: '/admin/config/printers',
      method: 'GET'
    });
  });

  app.post('/admin/config/printers', async (req, res) => {
    return sendProxyRequest({
      req,
      res,
      fetchImpl,
      apiBaseUrl,
      upstreamPath: '/admin/config/printers',
      method: 'POST'
    });
  });

  app.patch('/admin/config/printers/:printerId', async (req, res) => {
    return sendProxyRequest({
      req,
      res,
      fetchImpl,
      apiBaseUrl,
      upstreamPath: `/admin/config/printers/${req.params.printerId}`,
      method: 'PATCH'
    });
  });

  app.delete('/admin/config/printers/:printerId', async (req, res) => {
    return sendProxyRequest({
      req,
      res,
      fetchImpl,
      apiBaseUrl,
      upstreamPath: `/admin/config/printers/${req.params.printerId}`,
      method: 'DELETE'
    });
  });

  return app;
}
