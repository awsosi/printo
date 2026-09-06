-- Page thumbnails, and the removal of a table the rule bundle superseded.
--
-- Two changes, both closing loose ends left by 0012.

-- 1. Thumbnails for the review queue.
--
-- Plan section 6.6: a fallback is only actionable if an administrator can see the page that
-- caused it. `fallback_events.thumbnails_ref` has existed since 0012 with nothing to point at.
--
-- Stored as rows rather than in an object store, deliberately. These are a handful of ~260 px
-- PNGs per fallback - tens of kilobytes - and putting them here means they inherit the cascade
-- that already deletes a job's pages and traces, and the retention sweep that already prunes by
-- age. A blob store would need its own lifecycle, and an orphaned image nobody deletes is worse
-- than a slightly larger table.
CREATE TABLE IF NOT EXISTS agent_job_artifacts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  agent_job_id UUID NOT NULL REFERENCES agent_jobs(id) ON DELETE CASCADE,
  page_number INTEGER NOT NULL,
  kind TEXT NOT NULL DEFAULT 'thumbnail' CHECK (kind IN ('thumbnail')),
  content_type TEXT NOT NULL DEFAULT 'image/png',
  bytes BYTEA NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  -- One image per page per kind. A re-reported job replaces rather than accumulates.
  UNIQUE(agent_job_id, page_number, kind)
);

CREATE INDEX IF NOT EXISTS idx_agent_job_artifacts_job
  ON agent_job_artifacts(agent_job_id, page_number);

-- 2. `label_templates` is dropped.
--
-- Plan section 6.3 describes a LabelTemplate library: per carrier and variant, how to detect
-- the label and where its region sits. Everything it would hold is already a page rule in the
-- published bundle - `when` is the detection, `then.transform.source` is the region, and
-- `then.transform.media` is the stock - and the bundle is what agents actually download and
-- execute.
--
-- Keeping both would mean two answers to "how do I recognise a DHL label", which is precisely
-- the drift the shared conformance suite exists to prevent between the two engines. The library
-- is therefore a *view* of the rules grouped by carrier, not a second store, and this table has
-- never been read or written.
DROP TABLE IF EXISTS label_templates;
