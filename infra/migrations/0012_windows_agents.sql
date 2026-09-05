-- Windows agent fleet: enrolment, rule bundles, and the job/trace records the agents report.
--
-- Everything the workstation does has to be answerable from here: which machine printed what,
-- which rule decided each page, what the rule measured, and - when the engine could not decide
-- - what the person actually chose. That last part is the training signal the review queue
-- turns into a proposed rule, so it is stored as data rather than as a log line.
--
-- The existing SMB/CUPS path (processed_files, print_jobs, print_job_pages) is untouched: the
-- agent is additive, and sites will run both for a long time.

-- ---------------------------------------------------------------------------------------------
-- Enrolment
-- ---------------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS agent_enrollment_tokens (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  token_hash TEXT NOT NULL UNIQUE,
  label TEXT,
  expires_at TIMESTAMPTZ NOT NULL,
  -- One-time by default: a token that stays valid after use is a standing invitation to
  -- enrol an unmanaged machine into the print fleet.
  max_uses INTEGER NOT NULL DEFAULT 1,
  used_count INTEGER NOT NULL DEFAULT 0,
  created_by UUID REFERENCES users(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  revoked_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_agent_tokens_expiry ON agent_enrollment_tokens(expires_at);

CREATE TABLE IF NOT EXISTS agents (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  machine_name TEXT NOT NULL,
  -- Stable per install, so a renamed machine stays the same agent and a re-imaged one does not.
  install_id TEXT NOT NULL UNIQUE,
  os_version TEXT,
  agent_version TEXT,
  last_user TEXT,
  decision_mode TEXT NOT NULL DEFAULT 'auto'
    CHECK (decision_mode IN ('local', 'server', 'auto')),
  confidence_threshold DOUBLE PRECISION NOT NULL DEFAULT 0.75,
  bundle_version INTEGER,
  status TEXT NOT NULL DEFAULT 'ACTIVE'
    CHECK (status IN ('ACTIVE', 'DISABLED', 'RETIRED')),
  enrolled_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_seen_at TIMESTAMPTZ,
  -- Hashed like any other credential; the plaintext exists only on the workstation.
  api_key_hash TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_agents_last_seen ON agents(last_seen_at);
CREATE INDEX IF NOT EXISTS idx_agents_status ON agents(status);

CREATE TABLE IF NOT EXISTS agent_printers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  agent_id UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  queue_name TEXT NOT NULL,
  driver_name TEXT,
  port_name TEXT,
  role TEXT NOT NULL DEFAULT 'A4' CHECK (role IN ('A4', 'THERMAL', 'ALIAS')),
  alias TEXT,
  media TEXT,
  dpi DOUBLE PRECISION,
  -- Calibration belongs to the device, not the rules: a head 1.5mm off centre is off for
  -- every label it ever prints.
  offset_x_mm DOUBLE PRECISION NOT NULL DEFAULT 0,
  offset_y_mm DOUBLE PRECISION NOT NULL DEFAULT 0,
  zoom_percent DOUBLE PRECISION,
  darkness INTEGER,
  speed INTEGER,
  raw_zpl BOOLEAN NOT NULL DEFAULT FALSE,
  capabilities JSONB NOT NULL DEFAULT '{}'::jsonb,
  reported_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(agent_id, queue_name)
);

-- ---------------------------------------------------------------------------------------------
-- Rules and bundles
-- ---------------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS routing_rule_sets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  -- The `RoutingProfileRules` document, exactly as both engines consume it.
  rules JSONB NOT NULL,
  version INTEGER NOT NULL DEFAULT 1,
  is_published BOOLEAN NOT NULL DEFAULT FALSE,
  published_at TIMESTAMPTZ,
  created_by UUID REFERENCES users(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(name, version)
);
CREATE INDEX IF NOT EXISTS idx_rule_sets_published ON routing_rule_sets(is_published, published_at);

CREATE TABLE IF NOT EXISTS label_templates (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  carrier TEXT NOT NULL,
  variant TEXT NOT NULL DEFAULT 'default',
  detect JSONB NOT NULL DEFAULT '{}'::jsonb,
  region JSONB NOT NULL DEFAULT '{}'::jsonb,
  media TEXT,
  version INTEGER NOT NULL DEFAULT 1,
  is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(carrier, variant, version)
);

CREATE TABLE IF NOT EXISTS rule_bundles (
  version INTEGER PRIMARY KEY,
  -- Everything an agent needs in one document: profiles, templates, carrier signatures.
  payload JSONB NOT NULL,
  -- Agents verify this before applying a bundle, so a truncated download cannot change routing.
  checksum TEXT NOT NULL,
  published_by UUID REFERENCES users(id) ON DELETE SET NULL,
  published_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  notes TEXT
);

-- ---------------------------------------------------------------------------------------------
-- Jobs the agents report
-- ---------------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS agent_jobs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  agent_id UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  -- The agent's own idempotency key, so a re-report cannot create a second job here either.
  job_key TEXT NOT NULL,
  source TEXT NOT NULL CHECK (source IN ('HotFolder', 'VirtualPrinter', 'Reprint')),
  source_detail TEXT,
  file_name TEXT NOT NULL,
  doc_sha256 TEXT NOT NULL,
  page_count INTEGER NOT NULL DEFAULT 0,
  user_name TEXT,
  status TEXT NOT NULL,
  bundle_version INTEGER,
  error TEXT,
  started_at TIMESTAMPTZ,
  finished_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(agent_id, job_key)
);
CREATE INDEX IF NOT EXISTS idx_agent_jobs_created ON agent_jobs(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_agent_jobs_status ON agent_jobs(status);
CREATE INDEX IF NOT EXISTS idx_agent_jobs_sha ON agent_jobs(doc_sha256);

CREATE TABLE IF NOT EXISTS agent_job_pages (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  agent_job_id UUID NOT NULL REFERENCES agent_jobs(id) ON DELETE CASCADE,
  page_number INTEGER NOT NULL,
  page_class TEXT,
  carrier TEXT,
  confidence DOUBLE PRECISION,
  rule_id TEXT,
  route TEXT,
  printer_queue TEXT,
  -- The resolved transform, including which precedence layer supplied the media, so
  -- "why did it print at that size" is answerable from the job alone.
  transform JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(agent_job_id, page_number)
);

CREATE TABLE IF NOT EXISTS agent_job_events (
  id BIGSERIAL PRIMARY KEY,
  agent_job_id UUID NOT NULL REFERENCES agent_jobs(id) ON DELETE CASCADE,
  occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  level TEXT NOT NULL CHECK (level IN ('info', 'warning', 'error')),
  code TEXT NOT NULL,
  detail JSONB
);
CREATE INDEX IF NOT EXISTS idx_agent_job_events_job ON agent_job_events(agent_job_id, id);

CREATE TABLE IF NOT EXISTS agent_job_page_traces (
  id BIGSERIAL PRIMARY KEY,
  agent_job_page_id UUID NOT NULL REFERENCES agent_job_pages(id) ON DELETE CASCADE,
  rule_id TEXT NOT NULL,
  outcome TEXT NOT NULL CHECK (outcome IN ('matched', 'failed', 'skipped')),
  -- The predicate that decided it and the value it measured. Without these a fallback is
  -- unactionable: "routing failed" cannot be turned into a rule change.
  failed_predicate TEXT,
  measured JSONB
);
CREATE INDEX IF NOT EXISTS idx_page_traces_page ON agent_job_page_traces(agent_job_page_id);

-- ---------------------------------------------------------------------------------------------
-- Fallbacks and review
-- ---------------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS fallback_events (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  agent_job_id UUID NOT NULL REFERENCES agent_jobs(id) ON DELETE CASCADE,
  reason_code TEXT NOT NULL,
  message TEXT,
  -- What the engine proposed and what the person actually chose. The difference between them
  -- is the whole point: it is what turns a repeated fallback into a proposed rule.
  engine_selection INTEGER[] NOT NULL DEFAULT '{}',
  user_selection INTEGER[],
  resolution TEXT CHECK (resolution IS NULL OR resolution IN ('print', 'allA4', 'unanswered')),
  -- How long the person took; a slow answer says as much about a bad rule as the answer does.
  decision_ms BIGINT,
  trace JSONB,
  thumbnails_ref TEXT,
  raised_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  resolved_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_fallback_reason ON fallback_events(reason_code, raised_at DESC);
CREATE INDEX IF NOT EXISTS idx_fallback_job ON fallback_events(agent_job_id);

CREATE TABLE IF NOT EXISTS review_queue (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  agent_job_page_id UUID REFERENCES agent_job_pages(id) ON DELETE CASCADE,
  fallback_event_id UUID REFERENCES fallback_events(id) ON DELETE CASCADE,
  reason TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN', 'RESOLVED', 'DISMISSED')),
  resolution TEXT,
  proposed_rule JSONB,
  resolved_by UUID REFERENCES users(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  resolved_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_review_queue_status ON review_queue(status, created_at DESC);

-- ---------------------------------------------------------------------------------------------
-- Retention
-- ---------------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS retention_policies (
  scope TEXT PRIMARY KEY
    CHECK (scope IN ('documents', 'thumbnails', 'traces', 'job_history', 'audit', 'fallbacks')),
  retain_days INTEGER NOT NULL,
  last_run_at TIMESTAMPTZ,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Defaults chosen so the useful signal outlives the bulky data: traces and job history are
-- small and drive the analytics, while documents and thumbnails are large and only needed
-- while a job is fresh enough to re-examine.
INSERT INTO retention_policies (scope, retain_days) VALUES
  ('documents', 14),
  ('thumbnails', 30),
  ('traces', 90),
  ('job_history', 365),
  ('audit', 365),
  ('fallbacks', 365)
ON CONFLICT (scope) DO NOTHING;
