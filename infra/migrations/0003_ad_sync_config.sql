CREATE TABLE IF NOT EXISTS ad_sync_config (
  id BOOLEAN PRIMARY KEY DEFAULT TRUE,
  enabled BOOLEAN NOT NULL DEFAULT FALSE,
  server_url TEXT NOT NULL DEFAULT '',
  domain TEXT NOT NULL DEFAULT '',
  base_dn TEXT NOT NULL DEFAULT '',
  bind_username TEXT NOT NULL DEFAULT '',
  bind_secret_ref TEXT NOT NULL DEFAULT '',
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CHECK (id = TRUE)
);

INSERT INTO ad_sync_config (id, enabled, server_url, domain, base_dn, bind_username, bind_secret_ref)
VALUES (TRUE, FALSE, '', '', '', '', '')
ON CONFLICT (id) DO NOTHING;
