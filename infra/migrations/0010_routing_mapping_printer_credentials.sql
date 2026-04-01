ALTER TABLE routing_profiles
  ADD COLUMN IF NOT EXISTS printer_domain_username TEXT NOT NULL DEFAULT '',
  ADD COLUMN IF NOT EXISTS printer_secret_ref TEXT NOT NULL DEFAULT '';

ALTER TABLE smb_sources
  ADD COLUMN IF NOT EXISTS printer_domain_username TEXT NOT NULL DEFAULT '',
  ADD COLUMN IF NOT EXISTS printer_secret_ref TEXT NOT NULL DEFAULT '';
