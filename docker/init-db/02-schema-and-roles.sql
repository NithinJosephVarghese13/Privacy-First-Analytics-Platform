-- Create non-superuser application role
DO $$
BEGIN
   IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'analytics_app') THEN
      CREATE ROLE analytics_app WITH LOGIN PASSWORD 'analytics_dev' NOSUPERUSER;
   END IF;
END
$$;

GRANT CONNECT ON DATABASE analytics TO analytics_app;
GRANT USAGE ON SCHEMA public TO analytics_app;

-- Organizations table
CREATE TABLE IF NOT EXISTS organizations (
    org_id uuid NOT NULL CONSTRAINT PK_organizations PRIMARY KEY,
    name character varying(256) NOT NULL,
    public_write_key uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'::uuid
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_organizations_public_write_key" ON organizations (public_write_key);

-- Analytics events table (hypertable)
CREATE TABLE IF NOT EXISTS analytics_events (
    event_id uuid NOT NULL,
    timestamp timestamp with time zone NOT NULL,
    organization_id uuid NOT NULL,
    anonymous_daily_hash character varying(128),
    durable_hash character varying(128),
    is_authenticated boolean NOT NULL,
    event_type character varying(50) NOT NULL,
    path character varying(2048) NOT NULL,
    CONSTRAINT PK_analytics_events PRIMARY KEY (event_id, timestamp),
    CONSTRAINT fk_analytics_events_organization_id FOREIGN KEY (organization_id) REFERENCES organizations(org_id) ON DELETE CASCADE
);

-- Erasure audit log
CREATE TABLE IF NOT EXISTS erasure_audit_log (
    audit_id uuid NOT NULL CONSTRAINT PK_erasure_audit_log PRIMARY KEY,
    organization_id uuid NOT NULL,
    purged_identifier_hash character varying(128) NOT NULL,
    requested_by character varying(256) NOT NULL,
    purged_at_utc timestamp with time zone NOT NULL,
    records_affected integer NOT NULL
);

-- TimescaleDB hypertable
SELECT create_hypertable('analytics_events', 'timestamp', if_not_exists => TRUE, migrate_data => TRUE);

-- Row-Level Security
ALTER TABLE analytics_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE analytics_events FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tenant_isolation_select ON analytics_events;
CREATE POLICY tenant_isolation_select ON analytics_events FOR SELECT USING (organization_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

DROP POLICY IF EXISTS tenant_isolation_insert ON analytics_events;
CREATE POLICY tenant_isolation_insert ON analytics_events FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

DROP POLICY IF EXISTS tenant_isolation_delete ON analytics_events;
CREATE POLICY tenant_isolation_delete ON analytics_events FOR DELETE USING (organization_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

ALTER TABLE erasure_audit_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE erasure_audit_log FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tenant_isolation_select ON erasure_audit_log;
CREATE POLICY tenant_isolation_select ON erasure_audit_log FOR SELECT USING (organization_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

DROP POLICY IF EXISTS tenant_isolation_insert ON erasure_audit_log;
CREATE POLICY tenant_isolation_insert ON erasure_audit_log FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

-- Restricted Reporting Views (security_invoker = true)
CREATE OR REPLACE VIEW reporting_analytics_events WITH (security_invoker = true) AS
SELECT event_id, organization_id, timestamp, event_type, path, is_authenticated FROM analytics_events;

CREATE OR REPLACE VIEW reporting_daily_pageviews WITH (security_invoker = true) AS
SELECT organization_id, date_trunc('day', timestamp) AS event_date, path, event_type, COUNT(*) AS total_events FROM analytics_events GROUP BY organization_id, date_trunc('day', timestamp), path, event_type;

CREATE OR REPLACE VIEW reporting_top_pages WITH (security_invoker = true) AS
SELECT organization_id, path, event_type, COUNT(*) AS total_events FROM analytics_events GROUP BY organization_id, path, event_type;

-- Grants for analytics_app
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO analytics_app;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO analytics_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO analytics_app;

-- Seed default tenant organizations & sample events
INSERT INTO organizations (org_id, name, public_write_key)
VALUES 
    ('00000000-0000-0000-0000-000000000001', 'Acme Corp (Tenant A)', '00000000-0000-0000-0000-000000000001'),
    ('00000000-0000-0000-0000-000000000002', 'Stark Industries (Tenant B)', '00000000-0000-0000-0000-000000000002')
ON CONFLICT (org_id) DO NOTHING;

INSERT INTO analytics_events (event_id, timestamp, organization_id, anonymous_daily_hash, durable_hash, is_authenticated, event_type, path)
VALUES
    (gen_random_uuid(), NOW() - INTERVAL '1 day', '00000000-0000-0000-0000-000000000001', 'anon_hash_a1', 'durable_hash_a1', true, 'pageview', '/dashboard'),
    (gen_random_uuid(), NOW() - INTERVAL '2 hours', '00000000-0000-0000-0000-000000000001', 'anon_hash_a2', 'durable_hash_a2', true, 'pageview', '/pricing'),
    (gen_random_uuid(), NOW() - INTERVAL '3 hours', '00000000-0000-0000-0000-000000000001', 'anon_hash_a3', NULL, false, 'pageview', '/features'),
    (gen_random_uuid(), NOW() - INTERVAL '1 day', '00000000-0000-0000-0000-000000000002', 'anon_hash_b1', 'durable_hash_b1', true, 'pageview', '/secret-b-portal'),
    (gen_random_uuid(), NOW() - INTERVAL '2 hours', '00000000-0000-0000-0000-000000000002', 'anon_hash_b2', 'durable_hash_b2', true, 'pageview', '/confidential-b');
