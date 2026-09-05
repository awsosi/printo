import type { JsonObject } from '../types.js';

/** How an agent decides where a page goes. */
export type AgentDecisionMode = 'local' | 'server' | 'auto';

export type AgentStatus = 'ACTIVE' | 'DISABLED' | 'RETIRED';

export type AgentPrinterRole = 'A4' | 'THERMAL' | 'ALIAS';

/** An enrolled workstation. */
export interface AgentRecord {
  id: string;
  machineName: string;
  /**
   * Stable per install. A renamed machine stays the same agent; a re-imaged one becomes a new
   * one, which is what an admin means when they ask "is this the same PC".
   */
  installId: string;
  osVersion: string | null;
  agentVersion: string | null;
  lastUser: string | null;
  decisionMode: AgentDecisionMode;
  confidenceThreshold: number;
  bundleVersion: number | null;
  status: AgentStatus;
  enrolledAt: string;
  lastSeenAt: string | null;
}

/** A printer as the agent reported it. */
export interface AgentPrinterRecord {
  id: string;
  agentId: string;
  queueName: string;
  driverName: string | null;
  portName: string | null;
  role: AgentPrinterRole;
  alias: string | null;
  media: string | null;
  dpi: number | null;
  offsetXMm: number;
  offsetYMm: number;
  zoomPercent: number | null;
  darkness: number | null;
  speed: number | null;
  rawZpl: boolean;
  capabilities: JsonObject;
  reportedAt: string;
}

/** A published, versioned rule set. */
export interface RoutingRuleSetRecord {
  id: string;
  name: string;
  rules: JsonObject;
  version: number;
  isPublished: boolean;
  publishedAt: string | null;
  createdAt: string;
}

/** What an agent downloads and executes. */
export interface RuleBundleRecord {
  version: number;
  payload: JsonObject;
  checksum: string;
  publishedAt: string;
  notes: string | null;
}

/** A job an agent reported. */
export interface AgentJobRecord {
  id: string;
  agentId: string;
  jobKey: string;
  source: 'HotFolder' | 'VirtualPrinter' | 'Reprint';
  sourceDetail: string | null;
  fileName: string;
  documentSha256: string;
  pageCount: number;
  userName: string | null;
  status: string;
  bundleVersion: number | null;
  error: string | null;
  createdAt: string;
  updatedAt: string;
}

/** One page's outcome within a job. */
export interface AgentJobPageInput {
  pageNumber: number;
  pageClass?: string | null;
  carrier?: string | null;
  confidence?: number | null;
  ruleId?: string | null;
  route?: string | null;
  printerQueue?: string | null;
  transform?: JsonObject | null;
  /** Which rules were tried, and what the failing predicate measured. */
  traces?: AgentPageTraceInput[];
}

export interface AgentPageTraceInput {
  ruleId: string;
  outcome: 'matched' | 'failed' | 'skipped';
  failedPredicate?: string | null;
  measured?: JsonObject | null;
}

/** A picker event, with what the engine proposed and what the user actually chose. */
export interface FallbackEventInput {
  reasonCode: string;
  message?: string | null;
  engineSelection: number[];
  userSelection?: number[] | null;
  resolution?: 'print' | 'allA4' | 'unanswered' | null;
  decisionMs?: number | null;
  trace?: JsonObject | null;
  thumbnailsRef?: string | null;
}

export interface FallbackEventRecord extends FallbackEventInput {
  id: string;
  agentJobId: string;
  raisedAt: string;
  resolvedAt: string | null;
}

/** Aggregated fallbacks, the view that drives the rate towards zero. */
export interface FallbackSummaryRow {
  reasonCode: string;
  total: number;
  answered: number;
  /** Times the user agreed with the engine's proposal exactly. */
  agreedWithEngine: number;
  medianDecisionMs: number | null;
}

export interface ReviewQueueRecord {
  id: string;
  agentJobPageId: string | null;
  fallbackEventId: string | null;
  reason: string;
  status: 'OPEN' | 'RESOLVED' | 'DISMISSED';
  resolution: string | null;
  proposedRule: JsonObject | null;
  createdAt: string;
  resolvedAt: string | null;
}

export interface RetentionPolicyRecord {
  scope: string;
  retainDays: number;
  lastRunAt: string | null;
}
