import net from 'node:net';
import tls from 'node:tls';
import { randomUUID } from 'node:crypto';
import { resolveSecretFromRef } from './scanner/smb-path.js';

export interface SmtpSettings {
  smtpEnabled: boolean;
  smtpHost: string;
  smtpPort: number;
  smtpSecure: boolean;
  smtpUsername: string;
  smtpSecretRef: string;
  smtpFrom: string;
  smtpTo: string[];
}

export interface NotificationAttemptRecord {
  id: string;
  createdAt: string;
  category: 'PIPELINE_FAILURE' | 'TEST';
  dedupeKey: string | null;
  recipientCount: number;
  subject: string;
  status: 'SUCCESS' | 'FAILURE' | 'SKIPPED';
  errorMessage: string | null;
}

export interface NotificationPayload {
  category: 'PIPELINE_FAILURE' | 'TEST';
  dedupeKey?: string;
  subject: string;
  text: string;
}

interface PreparedMessage {
  from: string;
  to: string[];
  username: string;
  password: string | null;
  secure: boolean;
  host: string;
  port: number;
}

interface SmtpConnection {
  send(line: string): Promise<void>;
  sendData(data: string): Promise<void>;
  read(): Promise<string>;
  close(): void;
}

export interface SmtpTransport {
  sendMail(input: {
    connection: PreparedMessage;
    subject: string;
    text: string;
  }): Promise<void>;
}

function normalizeRecipients(value: string[]): string[] {
  return value.map((entry) => entry.trim()).filter(Boolean);
}

function formatDateHeader(value: Date): string {
  return value.toUTCString();
}

function escapeHeader(value: string): string {
  return value.replace(/[\r\n]+/g, ' ').trim();
}

function dotStuff(value: string): string {
  return value
    .replace(/\r?\n/g, '\r\n')
    .split('\r\n')
    .map((line) => (line.startsWith('.') ? `.${line}` : line))
    .join('\r\n');
}

function isPositiveCompletion(response: string): boolean {
  const code = Number(response.slice(0, 3));
  return Number.isFinite(code) && code >= 200 && code < 400;
}

async function expectOk(connection: SmtpConnection, command: string): Promise<string> {
  await connection.send(command);
  const response = await connection.read();
  if (!isPositiveCompletion(response)) {
    throw new Error(`SMTP_COMMAND_FAILED:${command}:${response.split('\n')[0] ?? 'UNKNOWN'}`);
  }
  return response;
}

class SocketSmtpConnection implements SmtpConnection {
  private buffer = '';
  private readonly pending: Array<(value: string) => void> = [];
  private readonly onError: (error: Error) => void;

  constructor(private readonly socket: net.Socket | tls.TLSSocket) {
    this.onError = (error) => {
      while (this.pending.length > 0) {
        const resolve = this.pending.shift();
        resolve?.(`500 ${error.message}`);
      }
    };

    socket.setEncoding('utf8');
    socket.on('data', (chunk: string) => {
      this.buffer += chunk;
      this.flush();
    });
    socket.on('error', this.onError);
    socket.on('close', () => {
      while (this.pending.length > 0) {
        const resolve = this.pending.shift();
        resolve?.(this.buffer || '421 CONNECTION_CLOSED');
      }
    });
  }

  async send(line: string): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      this.socket.write(`${line}\r\n`, (error) => {
        if (error) {
          reject(error);
          return;
        }
        resolve();
      });
    });
  }

  async sendData(data: string): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      this.socket.write(data, (error) => {
        if (error) {
          reject(error);
          return;
        }
        resolve();
      });
    });
  }

  async read(): Promise<string> {
    const current = this.extractResponse();
    if (current) {
      return current;
    }

    return new Promise((resolve) => {
      this.pending.push(resolve);
    });
  }

  close(): void {
    this.socket.removeListener('error', this.onError);
    this.socket.destroy();
  }

  private flush(): void {
    while (this.pending.length > 0) {
      const response = this.extractResponse();
      if (!response) {
        return;
      }
      const resolve = this.pending.shift();
      resolve?.(response);
    }
  }

  private extractResponse(): string | null {
    const lines = this.buffer.split('\r\n');
    if (lines.length < 2) {
      return null;
    }

    const collected: string[] = [];
    for (let index = 0; index < lines.length - 1; index += 1) {
      const line = lines[index] ?? '';
      collected.push(line);
      if (/^\d{3} /.test(line)) {
        this.buffer = lines.slice(index + 1).join('\r\n');
        return collected.join('\n');
      }
      if (!/^\d{3}-/.test(line)) {
        this.buffer = lines.slice(index + 1).join('\r\n');
        return collected.join('\n');
      }
    }

    return null;
  }
}

export class SocketSmtpTransport implements SmtpTransport {
  constructor(private readonly timeoutMs = 10_000) {}

  async sendMail(input: {
    connection: PreparedMessage;
    subject: string;
    text: string;
  }): Promise<void> {
    const connection = await this.connect(input.connection);
    try {
      const banner = await connection.read();
      if (!isPositiveCompletion(banner)) {
        throw new Error(`SMTP_CONNECT_FAILED:${banner.split('\n')[0] ?? 'UNKNOWN'}`);
      }

      await expectOk(connection, `EHLO printo.local`);

      if (input.connection.username) {
        await expectOk(connection, 'AUTH LOGIN');
        await expectOk(connection, Buffer.from(input.connection.username, 'utf8').toString('base64'));
        await expectOk(connection, Buffer.from(input.connection.password ?? '', 'utf8').toString('base64'));
      }

      await expectOk(connection, `MAIL FROM:<${input.connection.from}>`);
      for (const recipient of input.connection.to) {
        await expectOk(connection, `RCPT TO:<${recipient}>`);
      }
      await expectOk(connection, 'DATA');

      const now = new Date();
      const body = [
        `Date: ${formatDateHeader(now)}`,
        `From: ${escapeHeader(input.connection.from)}`,
        `To: ${input.connection.to.map((entry) => escapeHeader(entry)).join(', ')}`,
        `Subject: ${escapeHeader(input.subject)}`,
        `Message-ID: <${randomUUID()}@printo.local>`,
        'MIME-Version: 1.0',
        'Content-Type: text/plain; charset=utf-8',
        'Content-Transfer-Encoding: 8bit',
        '',
        dotStuff(input.text),
        '.',
        ''
      ].join('\r\n');
      await connection.sendData(body);

      const dataResponse = await connection.read();
      if (!isPositiveCompletion(dataResponse)) {
        throw new Error(`SMTP_DATA_FAILED:${dataResponse.split('\n')[0] ?? 'UNKNOWN'}`);
      }

      await expectOk(connection, 'QUIT');
    } finally {
      connection.close();
    }
  }

  private async connect(input: PreparedMessage): Promise<SmtpConnection> {
    const socket = input.secure
      ? tls.connect({
          host: input.host,
          port: input.port,
          servername: input.host,
          timeout: this.timeoutMs
        })
      : net.createConnection({
          host: input.host,
          port: input.port
        });

    socket.setTimeout(this.timeoutMs, () => {
      socket.destroy(new Error(`SMTP_TIMEOUT:${input.host}:${input.port}`));
    });

    await new Promise<void>((resolve, reject) => {
      socket.once('connect', () => resolve());
      socket.once('secureConnect', () => resolve());
      socket.once('error', reject);
    });

    return new SocketSmtpConnection(socket);
  }
}

export class SmtpNotificationService {
  private readonly recentAttempts: NotificationAttemptRecord[] = [];
  private readonly sentByDedupeKey = new Map<string, number>();

  constructor(
    private readonly settingsLoader: () => Promise<SmtpSettings>,
    private readonly transport: SmtpTransport = new SocketSmtpTransport(),
    private readonly throttleWindowMs = 10 * 60_000,
    private readonly historyLimit = 200
  ) {}

  listAttempts(limit = 100): NotificationAttemptRecord[] {
    return this.recentAttempts.slice(0, Math.max(1, Math.min(limit, this.historyLimit))).map((entry) => ({ ...entry }));
  }

  async send(payload: NotificationPayload): Promise<NotificationAttemptRecord> {
    const settings = await this.settingsLoader();
    const recipients = normalizeRecipients(settings.smtpTo);
    const dedupeKey = payload.dedupeKey?.trim() || null;

    if (!settings.smtpEnabled || !settings.smtpHost.trim() || !settings.smtpFrom.trim() || recipients.length === 0) {
      return this.recordAttempt({
        category: payload.category,
        dedupeKey,
        recipientCount: recipients.length,
        subject: payload.subject,
        status: 'SKIPPED',
        errorMessage: 'SMTP_NOT_CONFIGURED'
      });
    }

    if (dedupeKey) {
      const previous = this.sentByDedupeKey.get(dedupeKey);
      if (previous && Date.now() - previous < this.throttleWindowMs) {
        return this.recordAttempt({
          category: payload.category,
          dedupeKey,
          recipientCount: recipients.length,
          subject: payload.subject,
          status: 'SKIPPED',
          errorMessage: 'THROTTLED_DUPLICATE_NOTIFICATION'
        });
      }
    }

    const password = settings.smtpUsername ? resolveSecretFromRef(settings.smtpSecretRef) : null;
    if (settings.smtpUsername && !password) {
      return this.recordAttempt({
        category: payload.category,
        dedupeKey,
        recipientCount: recipients.length,
        subject: payload.subject,
        status: 'FAILURE',
        errorMessage: 'SMTP_SECRET_UNRESOLVED'
      });
    }

    try {
      await this.transport.sendMail({
        connection: {
          from: settings.smtpFrom.trim(),
          to: recipients,
          username: settings.smtpUsername.trim(),
          password,
          secure: settings.smtpSecure,
          host: settings.smtpHost.trim(),
          port: settings.smtpPort
        },
        subject: payload.subject,
        text: payload.text
      });

      if (dedupeKey) {
        this.sentByDedupeKey.set(dedupeKey, Date.now());
      }

      return this.recordAttempt({
        category: payload.category,
        dedupeKey,
        recipientCount: recipients.length,
        subject: payload.subject,
        status: 'SUCCESS',
        errorMessage: null
      });
    } catch (error) {
      return this.recordAttempt({
        category: payload.category,
        dedupeKey,
        recipientCount: recipients.length,
        subject: payload.subject,
        status: 'FAILURE',
        errorMessage: error instanceof Error ? error.message : 'SMTP_SEND_FAILED'
      });
    }
  }

  private recordAttempt(input: Omit<NotificationAttemptRecord, 'id' | 'createdAt'>): NotificationAttemptRecord {
    const record: NotificationAttemptRecord = {
      id: randomUUID(),
      createdAt: new Date().toISOString(),
      ...input
    };
    this.recentAttempts.unshift(record);
    if (this.recentAttempts.length > this.historyLimit) {
      this.recentAttempts.length = this.historyLimit;
    }
    return record;
  }
}
