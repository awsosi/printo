-- The fleet policy: operational settings this server hands every Windows agent on its
-- heartbeat, and applies to what the worker routes itself.
--
-- Stored as the settings an administrator actually chose (JSONB, validated by the API), never
-- as a full copy of the defaults. An unset setting is then absent from what the agents are
-- sent, and each workstation reports "product default" for it rather than "set by the server"
-- for a value nobody on the server ever touched.
--
-- Additive: nothing here changes what an existing deployment does until somebody sets a value.
-- The defaults in code - waybill copies routed as before, log files off - are the behaviour
-- every site already had.

CREATE TABLE IF NOT EXISTS fleet_policy (
  id BOOLEAN PRIMARY KEY DEFAULT TRUE,
  policy JSONB NOT NULL DEFAULT '{}'::jsonb,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_by UUID REFERENCES users(id) ON DELETE SET NULL,
  CHECK (id = TRUE)
);

INSERT INTO fleet_policy (id) VALUES (TRUE) ON CONFLICT (id) DO NOTHING;

-- One machine can differ from the fleet - a bench with 100x150 stock, a pilot machine that
-- writes debug logs - without a Group Policy object of its own. Merged over the fleet policy
-- field by field; '{}' means "exactly the fleet".
ALTER TABLE agents ADD COLUMN IF NOT EXISTS policy_overrides JSONB NOT NULL DEFAULT '{}'::jsonb;

-- Which page each printer is sent first, as the agent reports it: auto, firstPageFirst or
-- lastPageFirst. Null from agents that predate it.
ALTER TABLE agent_printers ADD COLUMN IF NOT EXISTS page_order TEXT;
