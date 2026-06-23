-- Phase 7: link alert deliveries to monitors
ALTER TABLE alert_logs ADD COLUMN IF NOT EXISTS monitor_id UUID REFERENCES monitors(id) ON DELETE SET NULL;
