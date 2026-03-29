CREATE TABLE IF NOT EXISTS system_settings (
  id BOOLEAN PRIMARY KEY DEFAULT TRUE,
  global_smb_domain_username TEXT NOT NULL DEFAULT '',
  global_smb_secret_ref TEXT NOT NULL DEFAULT '',
  global_printer_domain_username TEXT NOT NULL DEFAULT '',
  global_printer_secret_ref TEXT NOT NULL DEFAULT '',
  worker_poll_interval_ms INTEGER NOT NULL DEFAULT 5000,
  smtp_enabled BOOLEAN NOT NULL DEFAULT FALSE,
  smtp_host TEXT NOT NULL DEFAULT '',
  smtp_port INTEGER NOT NULL DEFAULT 25,
  smtp_secure BOOLEAN NOT NULL DEFAULT FALSE,
  smtp_username TEXT NOT NULL DEFAULT '',
  smtp_secret_ref TEXT NOT NULL DEFAULT '',
  smtp_from TEXT NOT NULL DEFAULT '',
  smtp_to JSONB NOT NULL DEFAULT '[]'::jsonb,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CHECK (id = TRUE),
  CHECK (worker_poll_interval_ms >= 1000),
  CHECK (smtp_port >= 1 AND smtp_port <= 65535)
);

INSERT INTO system_settings (
  id,
  global_smb_domain_username,
  global_smb_secret_ref,
  global_printer_domain_username,
  global_printer_secret_ref,
  worker_poll_interval_ms,
  smtp_enabled,
  smtp_host,
  smtp_port,
  smtp_secure,
  smtp_username,
  smtp_secret_ref,
  smtp_from,
  smtp_to
)
VALUES (
  TRUE,
  '',
  '',
  '',
  '',
  5000,
  FALSE,
  '',
  25,
  FALSE,
  '',
  '',
  '',
  '[]'::jsonb
)
ON CONFLICT (id) DO NOTHING;
