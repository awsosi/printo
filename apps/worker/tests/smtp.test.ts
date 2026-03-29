import { describe, expect, it, vi } from 'vitest';
import { SmtpNotificationService, type SmtpSettings } from '../src/smtp.js';

function createSettings(overrides: Partial<SmtpSettings> = {}): SmtpSettings {
  return {
    smtpEnabled: true,
    smtpHost: 'smtp.example.test',
    smtpPort: 25,
    smtpSecure: false,
    smtpUsername: '',
    smtpSecretRef: '',
    smtpFrom: 'printo@example.test',
    smtpTo: ['admin@example.test'],
    ...overrides
  };
}

describe('smtp notification service', () => {
  it('sends via transport and records success', async () => {
    const transport = {
      sendMail: vi.fn(async () => undefined)
    };
    const service = new SmtpNotificationService(async () => createSettings(), transport);

    const result = await service.send({
      category: 'TEST',
      subject: 'subject',
      text: 'body'
    });

    expect(result.status).toBe('SUCCESS');
    expect(transport.sendMail).toHaveBeenCalledTimes(1);
    expect(service.listAttempts(10)).toHaveLength(1);
  });

  it('throttles duplicate failure notifications', async () => {
    const transport = {
      sendMail: vi.fn(async () => undefined)
    };
    const service = new SmtpNotificationService(async () => createSettings(), transport, 60_000);

    const first = await service.send({
      category: 'PIPELINE_FAILURE',
      dedupeKey: 'job:1:error',
      subject: 'failure',
      text: 'body'
    });
    const second = await service.send({
      category: 'PIPELINE_FAILURE',
      dedupeKey: 'job:1:error',
      subject: 'failure',
      text: 'body'
    });

    expect(first.status).toBe('SUCCESS');
    expect(second.status).toBe('SKIPPED');
    expect(second.errorMessage).toBe('THROTTLED_DUPLICATE_NOTIFICATION');
    expect(transport.sendMail).toHaveBeenCalledTimes(1);
  });

  it('skips when smtp is not configured', async () => {
    const transport = {
      sendMail: vi.fn(async () => undefined)
    };
    const service = new SmtpNotificationService(
      async () =>
        createSettings({
          smtpEnabled: false,
          smtpHost: '',
          smtpFrom: '',
          smtpTo: []
        }),
      transport
    );

    const result = await service.send({
      category: 'TEST',
      subject: 'subject',
      text: 'body'
    });

    expect(result.status).toBe('SKIPPED');
    expect(result.errorMessage).toBe('SMTP_NOT_CONFIGURED');
    expect(transport.sendMail).not.toHaveBeenCalled();
  });
});
