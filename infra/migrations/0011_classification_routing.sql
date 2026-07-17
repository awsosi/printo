-- Classification-based routing (Vision Service / heuristic page classes).
-- classification_routes: ordered JSONB array of
--   { "pageClass": "OUTGOING_LABEL_THERMAL" | "RETURN_LABEL_A4" | "DOCUMENT_A4",
--     "routeType": "A4" | "THERMAL",
--     "printerId": uuid | null,
--     "minConfidence": 0..1 }
ALTER TABLE routing_profiles
  ADD COLUMN IF NOT EXISTS classification_routes JSONB NOT NULL DEFAULT '[]'::jsonb;

-- Per-page classification diagnostics for the admin UI / troubleshooting.
ALTER TABLE print_job_pages
  ADD COLUMN IF NOT EXISTS page_class TEXT
    CHECK (page_class IS NULL OR page_class IN ('OUTGOING_LABEL_THERMAL', 'RETURN_LABEL_A4', 'DOCUMENT_A4')),
  ADD COLUMN IF NOT EXISTS classification_confidence DOUBLE PRECISION,
  ADD COLUMN IF NOT EXISTS carrier TEXT;
