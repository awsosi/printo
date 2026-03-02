import net from 'node:net';
import type { DispatchRequest, PrinterDispatcher, WorkerPrinter } from '../pipeline.js';

export type DispatchProviderName = 'mock' | 'socket' | 'ipp';

interface DispatchResolution {
  provider: DispatchProviderName;
  targetUri: string;
  timeoutMs: number;
}

interface ProviderOverride {
  provider?: DispatchProviderName;
  targetUri?: string;
  timeoutMs?: number;
}

type ProviderOverrideMap = Record<string, ProviderOverride>;

interface DispatchProvider {
  dispatch(input: DispatchRequest, resolution: DispatchResolution): Promise<void>;
}

function toPayload(fileContent: Buffer | string): Buffer {
  if (Buffer.isBuffer(fileContent)) {
    return fileContent;
  }

  return Buffer.from(fileContent, 'utf8');
}

function normalizeDispatchUri(uri: string): string {
  if (uri.startsWith('ipp://')) {
    return `http://${uri.slice('ipp://'.length)}`;
  }

  if (uri.startsWith('ipps://')) {
    return `https://${uri.slice('ipps://'.length)}`;
  }

  return uri;
}

function detectProviderFromTargetUri(targetUri: string): DispatchProviderName {
  try {
    const protocol = new URL(targetUri).protocol.replace(':', '').toLowerCase();

    if (protocol === 'socket' || protocol === 'tcp') {
      return 'socket';
    }

    if (protocol === 'ipp' || protocol === 'ipps' || protocol === 'http' || protocol === 'https') {
      return 'ipp';
    }
  } catch {
    return 'mock';
  }

  return 'mock';
}

function parseOverrideMap(raw: string | undefined): ProviderOverrideMap {
  if (!raw) {
    return {};
  }

  try {
    const parsed = JSON.parse(raw) as Record<string, ProviderOverride>;
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

function resolveMode(rawMode: string | undefined): DispatchProviderName | 'auto' {
  const mode = String(rawMode ?? 'mock').toLowerCase();

  if (mode === 'auto' || mode === 'mock' || mode === 'socket' || mode === 'ipp') {
    return mode;
  }

  return 'mock';
}

export class MockDispatchProvider implements DispatchProvider {
  async dispatch(input: DispatchRequest, resolution: DispatchResolution): Promise<void> {
    // eslint-disable-next-line no-console
    console.log(
      JSON.stringify({
        service: 'worker',
        event: 'print_dispatch',
        provider: resolution.provider,
        routeType: input.routeType,
        printerId: input.printer.id,
        printerName: input.printer.name,
        filePath: input.file.path,
        pageNumber: input.page.pageNumber
      })
    );
  }
}

export class SocketDispatchProvider implements DispatchProvider {
  async dispatch(input: DispatchRequest, resolution: DispatchResolution): Promise<void> {
    const target = new URL(resolution.targetUri);
    const host = target.hostname;
    const port = target.port ? Number(target.port) : 9100;

    if (!host || Number.isNaN(port)) {
      throw new Error(`INVALID_SOCKET_URI:${resolution.targetUri}`);
    }

    const payload = toPayload(input.file.content);

    await new Promise<void>((resolve, reject) => {
      const socket = net.createConnection({ host, port });

      const cleanup = () => {
        socket.removeAllListeners('connect');
        socket.removeAllListeners('error');
        socket.removeAllListeners('timeout');
        socket.removeAllListeners('close');
      };

      socket.setTimeout(resolution.timeoutMs);

      socket.once('connect', () => {
        socket.write(payload, (error) => {
          if (error) {
            cleanup();
            socket.destroy();
            reject(error);
            return;
          }

          socket.end();
        });
      });

      socket.once('timeout', () => {
        cleanup();
        socket.destroy();
        reject(new Error(`SOCKET_TIMEOUT:${host}:${port}`));
      });

      socket.once('error', (error) => {
        cleanup();
        socket.destroy();
        reject(error);
      });

      socket.once('close', (hadError) => {
        cleanup();
        if (hadError) {
          return;
        }

        resolve();
      });
    });
  }
}

export class IppDispatchProvider implements DispatchProvider {
  constructor(private readonly fetchImpl: typeof fetch = fetch) {}

  async dispatch(input: DispatchRequest, resolution: DispatchResolution): Promise<void> {
    const payload = toPayload(input.file.content);
    const body = new Uint8Array(payload);
    const targetUrl = normalizeDispatchUri(resolution.targetUri);

    const response = await this.fetchImpl(targetUrl, {
      method: 'POST',
      headers: {
        'content-type': 'application/pdf',
        'x-printo-page-number': String(input.page.pageNumber),
        'x-printo-route-type': input.routeType,
        'x-printo-printer-id': input.printer.id
      },
      body,
      signal: AbortSignal.timeout(resolution.timeoutMs)
    });

    if (!response.ok) {
      throw new Error(`IPP_DISPATCH_FAILED:${response.status}`);
    }
  }
}

export interface ProviderPrinterDispatcherOptions {
  mode?: DispatchProviderName | 'auto';
  timeoutMs?: number;
  overrides?: ProviderOverrideMap;
  providers?: Partial<Record<DispatchProviderName, DispatchProvider>>;
}

export class ProviderPrinterDispatcher implements PrinterDispatcher {
  private readonly mode: DispatchProviderName | 'auto';
  private readonly timeoutMs: number;
  private readonly overrides: ProviderOverrideMap;
  private readonly providers: Record<DispatchProviderName, DispatchProvider>;

  constructor(options: ProviderPrinterDispatcherOptions = {}) {
    this.mode = options.mode ?? resolveMode(process.env.WORKER_DISPATCH_PROVIDER_MODE);
    this.timeoutMs = options.timeoutMs ?? Number(process.env.WORKER_DISPATCH_TIMEOUT_MS ?? 5_000);
    this.overrides = options.overrides ?? parseOverrideMap(process.env.WORKER_PRINTER_PROVIDER_OVERRIDES);

    this.providers = {
      mock: options.providers?.mock ?? new MockDispatchProvider(),
      socket: options.providers?.socket ?? new SocketDispatchProvider(),
      ipp: options.providers?.ipp ?? new IppDispatchProvider()
    };
  }

  private resolveDispatch(printer: WorkerPrinter): DispatchResolution {
    const override = this.overrides[printer.id] ?? this.overrides[printer.name] ?? {};
    const effectiveTargetUri = override.targetUri ?? printer.targetUri;
    const effectiveTimeoutMs = override.timeoutMs ?? this.timeoutMs;

    const provider = override.provider ?? (this.mode === 'auto' ? detectProviderFromTargetUri(effectiveTargetUri) : this.mode);

    return {
      provider,
      targetUri: effectiveTargetUri,
      timeoutMs: effectiveTimeoutMs
    };
  }

  async dispatch(input: DispatchRequest): Promise<void> {
    const resolution = this.resolveDispatch(input.printer);
    const provider = this.providers[resolution.provider];
    await provider.dispatch(input, resolution);
  }
}
