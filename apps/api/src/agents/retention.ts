import type { Pool } from 'pg';
import type { AgentStore } from './store.js';

/**
 * A mutual exclusion around the retention sweep.
 *
 * Retention deletes rows. Two API replicas sweeping the same tables at the same instant is
 * not corrupting - the statements are idempotent deletes - but it doubles the work on the
 * database at exactly the moment it is already doing the most, and it makes `last_run_at`
 * lie about which run produced which counts.
 */
export interface RetentionLock {
  /** Runs `work` if the lock was free; returns `null` when another holder has it. */
  run<T>(work: () => Promise<T>): Promise<T | null>;
}

/** A lock that is always free. The single-process default. */
export const UNLOCKED: RetentionLock = {
  run: async (work) => work()
};

/**
 * A Postgres session-level advisory lock.
 *
 * Advisory rather than a lock table because it needs no schema, is released automatically if
 * the holder's connection dies - a replica killed mid-sweep must not block every later run -
 * and costs nothing when uncontended.
 */
export class PostgresAdvisoryLock implements RetentionLock {
  /** Arbitrary but fixed: any other advisory lock in this database must not collide. */
  static readonly RETENTION_KEY = 0x7072_6e74;

  constructor(
    private readonly pool: Pool,
    private readonly key: number = PostgresAdvisoryLock.RETENTION_KEY
  ) {}

  async run<T>(work: () => Promise<T>): Promise<T | null> {
    // One client for the whole attempt: a session-level advisory lock belongs to the
    // connection that took it, so acquiring and releasing on different pool clients would
    // leak the lock until that connection happened to be recycled.
    const client = await this.pool.connect();
    try {
      const acquired = await client.query<{ locked: boolean }>(
        'SELECT pg_try_advisory_lock($1) AS locked',
        [this.key]
      );

      if (!acquired.rows[0]?.locked) {
        return null;
      }

      try {
        return await work();
      } finally {
        await client.query('SELECT pg_advisory_unlock($1)', [this.key]);
      }
    } finally {
      client.release();
    }
  }
}

export interface RetentionSchedulerOptions {
  /** How often the sweep runs. Defaults to daily. */
  intervalMs?: number;
  /**
   * How long after start the first sweep runs.
   *
   * Not zero: a deploy that restarts every replica at once would otherwise have all of them
   * sweeping while they are also serving the first requests of the day.
   */
  initialDelayMs?: number;
  lock?: RetentionLock;
  log?: (event: Record<string, unknown>) => void;
}

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Runs the retention sweep on a schedule.
 *
 * The policies are configured and the sweep is exposed over the admin API, but a policy that
 * only runs when somebody remembers to press a button is not a retention policy - it is a
 * button. This is what makes the configured windows true.
 *
 * Failures are logged and never thrown: a sweep that cannot run is a housekeeping problem,
 * and taking the API down over it would turn it into an outage.
 */
export class RetentionScheduler {
  private timer: NodeJS.Timeout | null = null;

  private running = false;

  private readonly intervalMs: number;

  private readonly initialDelayMs: number;

  private readonly lock: RetentionLock;

  private readonly log: (event: Record<string, unknown>) => void;

  constructor(
    private readonly store: AgentStore,
    options: RetentionSchedulerOptions = {}
  ) {
    this.intervalMs = options.intervalMs ?? DAY_MS;
    this.initialDelayMs = options.initialDelayMs ?? 5 * 60 * 1000;
    this.lock = options.lock ?? UNLOCKED;
    this.log =
      options.log ??
      // eslint-disable-next-line no-console
      ((event) => console.log(JSON.stringify({ service: 'api', ...event })));
  }

  /** Starts the schedule. The timer does not hold the process open. */
  start(): void {
    if (this.timer) {
      return;
    }

    const tick = (): void => {
      void this.runOnce();
    };

    this.timer = setTimeout(() => {
      tick();
      this.timer = setInterval(tick, this.intervalMs);
      this.timer.unref?.();
    }, this.initialDelayMs);

    this.timer.unref?.();
  }

  stop(): void {
    if (this.timer) {
      clearTimeout(this.timer);
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  /**
   * Runs one sweep.
   *
   * @returns rows removed per scope, or `null` when the sweep was skipped - another replica
   * holds the lock, or this one is still working through the previous run.
   */
  async runOnce(): Promise<Record<string, number> | null> {
    if (this.running) {
      // A sweep that outlives its own interval must not stack up behind itself.
      this.log({ event: 'retention_skipped', reason: 'already_running' });
      return null;
    }

    this.running = true;
    const started = Date.now();

    try {
      const removed = await this.lock.run(() => this.store.applyRetention());

      if (removed === null) {
        this.log({ event: 'retention_skipped', reason: 'locked' });
        return null;
      }

      this.log({
        event: 'retention_applied',
        durationMs: Date.now() - started,
        removed
      });

      return removed;
    } catch (error) {
      this.log({
        event: 'retention_failed',
        error: error instanceof Error ? error.message : 'RETENTION_ERROR'
      });
      return null;
    } finally {
      this.running = false;
    }
  }
}
