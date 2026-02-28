import { describe, expect, it, vi } from 'vitest';
import type { DispatchRequest } from '../src/pipeline.js';
import { ProviderPrinterDispatcher } from '../src/dispatch/provider-printer-dispatcher.js';

function buildRequest(targetUri: string): DispatchRequest {
  return {
    routeType: 'A4',
    printer: {
      id: 'printer-1',
      name: 'A4-main',
      type: 'A4',
      targetUri,
      isActive: true
    },
    file: {
      sourceId: 'source-1',
      path: '/in/a.pdf',
      content: Buffer.from('pdf-data'),
      modifiedAt: null
    },
    page: {
      pageNumber: 1,
      labels: []
    }
  };
}

describe('provider printer dispatcher', () => {
  it('uses mock provider by default', async () => {
    const mockProvider = { dispatch: vi.fn(async () => undefined) };

    const dispatcher = new ProviderPrinterDispatcher({
      providers: {
        mock: mockProvider,
        socket: { dispatch: vi.fn(async () => undefined) },
        ipp: { dispatch: vi.fn(async () => undefined) }
      }
    });

    await dispatcher.dispatch(buildRequest('socket://printer.local:9100'));
    expect(mockProvider.dispatch).toHaveBeenCalledTimes(1);
  });

  it('uses provider auto-detected from URI in auto mode', async () => {
    const socketProvider = { dispatch: vi.fn(async () => undefined) };

    const dispatcher = new ProviderPrinterDispatcher({
      mode: 'auto',
      providers: {
        mock: { dispatch: vi.fn(async () => undefined) },
        socket: socketProvider,
        ipp: { dispatch: vi.fn(async () => undefined) }
      }
    });

    await dispatcher.dispatch(buildRequest('socket://printer.local:9100'));
    expect(socketProvider.dispatch).toHaveBeenCalledTimes(1);
  });

  it('respects per-printer override config', async () => {
    const ippProvider = { dispatch: vi.fn(async () => undefined) };

    const dispatcher = new ProviderPrinterDispatcher({
      mode: 'auto',
      overrides: {
        'printer-1': {
          provider: 'ipp',
          targetUri: 'ipp://ipp.local/print',
          timeoutMs: 2500
        }
      },
      providers: {
        mock: { dispatch: vi.fn(async () => undefined) },
        socket: { dispatch: vi.fn(async () => undefined) },
        ipp: ippProvider
      }
    });

    await dispatcher.dispatch(buildRequest('socket://printer.local:9100'));

    expect(ippProvider.dispatch).toHaveBeenCalledTimes(1);
    const [, resolution] = ippProvider.dispatch.mock.calls[0] as [DispatchRequest, { targetUri: string; timeoutMs: number }];
    expect(resolution.targetUri).toBe('ipp://ipp.local/print');
    expect(resolution.timeoutMs).toBe(2500);
  });
});
