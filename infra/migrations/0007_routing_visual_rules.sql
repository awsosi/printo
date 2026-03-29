ALTER TABLE routing_profiles
  ADD COLUMN IF NOT EXISTS default_route_type TEXT NOT NULL DEFAULT 'A4'
  CHECK (default_route_type IN ('A4', 'THERMAL'));

ALTER TABLE routing_profiles
  ADD COLUMN IF NOT EXISTS sample_pdf_name TEXT;

ALTER TABLE routing_profiles
  ADD COLUMN IF NOT EXISTS sample_pdf_base64 TEXT;

ALTER TABLE routing_profiles
  ADD COLUMN IF NOT EXISTS visual_rules JSONB NOT NULL DEFAULT '[]'::jsonb;
