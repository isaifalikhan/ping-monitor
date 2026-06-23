-- Phase 5: device metadata for ERP / network infrastructure monitors
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS device_location TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS rack_cabinet TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS switch_port TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS device_owner TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS criticality TEXT DEFAULT 'MEDIUM'
  CHECK (criticality IN ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL'));
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS vendor TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS model TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS ip_address TEXT;
-- Future SNMP / interface fields (stored, not used by checks yet)
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS snmp_community TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS snmp_version TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS interface_name TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS interface_status TEXT;
ALTER TABLE monitors ADD COLUMN IF NOT EXISTS bandwidth_usage_mbps DOUBLE PRECISION;
