CREATE TABLE IF NOT EXISTS user_groups (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL UNIQUE,
  description TEXT,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS user_group_memberships (
  group_id UUID NOT NULL REFERENCES user_groups(id) ON DELETE CASCADE,
  user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (group_id, user_id)
);

ALTER TABLE smb_sources
  ADD COLUMN IF NOT EXISTS owner_group_id UUID REFERENCES user_groups(id) ON DELETE SET NULL;

ALTER TABLE filename_masks
  ADD COLUMN IF NOT EXISTS owner_group_id UUID REFERENCES user_groups(id) ON DELETE SET NULL;

ALTER TABLE routing_profiles
  ADD COLUMN IF NOT EXISTS owner_user_id UUID REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE routing_profiles
  ADD COLUMN IF NOT EXISTS owner_group_id UUID REFERENCES user_groups(id) ON DELETE SET NULL;

CREATE TABLE IF NOT EXISTS visual_match_profiles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  owner_user_id UUID REFERENCES users(id) ON DELETE SET NULL,
  owner_group_id UUID REFERENCES user_groups(id) ON DELETE SET NULL,
  snippet_base64 TEXT NOT NULL,
  match_mode TEXT NOT NULL CHECK (match_mode IN ('CONTAINS', 'EXACT')),
  route_type TEXT CHECK (route_type IN ('A4', 'THERMAL')),
  printer_id UUID REFERENCES printers(id) ON DELETE SET NULL,
  labels JSONB NOT NULL DEFAULT '[]'::jsonb,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
