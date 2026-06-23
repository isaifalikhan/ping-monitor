-- Phase 3: monitor category and port for internal infrastructure tracking
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS category TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS port INTEGER;
