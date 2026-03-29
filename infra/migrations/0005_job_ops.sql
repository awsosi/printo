ALTER TABLE print_jobs
  ADD COLUMN IF NOT EXISTS source_id UUID REFERENCES smb_sources(id) ON DELETE SET NULL;

ALTER TABLE print_jobs
  ADD COLUMN IF NOT EXISTS file_path TEXT NOT NULL DEFAULT '';

ALTER TABLE print_jobs
  ADD COLUMN IF NOT EXISTS file_checksum_sha256 TEXT NOT NULL DEFAULT '';

ALTER TABLE print_jobs
  ADD COLUMN IF NOT EXISTS file_mtime TIMESTAMPTZ;

ALTER TABLE print_jobs
  ADD COLUMN IF NOT EXISTS is_cancelled BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE print_job_pages
  ADD COLUMN IF NOT EXISTS error_message TEXT;

CREATE INDEX IF NOT EXISTS idx_print_jobs_identity
  ON print_jobs(file_checksum_sha256, file_path, file_mtime);
