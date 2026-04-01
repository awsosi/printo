ALTER TABLE smb_sources
  ADD COLUMN IF NOT EXISTS routing_profile_id UUID REFERENCES routing_profiles(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS a4_printer_id UUID REFERENCES printers(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS thermal_printer_id UUID REFERENCES printers(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS include_filename_patterns JSONB NOT NULL DEFAULT '[]'::jsonb,
  ADD COLUMN IF NOT EXISTS exclude_filename_patterns JSONB NOT NULL DEFAULT '[]'::jsonb;
