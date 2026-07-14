using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrivacyAnalytics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analytics_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anonymous_daily_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    durable_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    is_authenticated = table.Column<bool>(type: "boolean", nullable: false),
                    event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_events", x => new { x.event_id, x.timestamp });
                });

            migrationBuilder.CreateTable(
                name: "erasure_audit_log",
                columns: table => new
                {
                    audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purged_identifier_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    purged_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    records_affected = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erasure_audit_log", x => x.audit_id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.org_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_analytics_events_organization_id",
                table: "analytics_events",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_analytics_events_timestamp_desc",
                table: "analytics_events",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_erasure_audit_log_organization_id",
                table: "erasure_audit_log",
                column: "organization_id");

            migrationBuilder.Sql("""
                -- Convert analytics_events to TimescaleDB hypertable
                SELECT create_hypertable('analytics_events', 'timestamp',
                    if_not_exists => TRUE,
                    migrate_data => TRUE);

                -- Tenant context functions (fail-closed: NULL -> zero rows)
                CREATE OR REPLACE FUNCTION set_tenant(tenant_id uuid) RETURNS void AS $$
                BEGIN
                    PERFORM set_config('app.current_tenant_id', tenant_id::text, false);
                END;
                $$ LANGUAGE plpgsql SECURITY DEFINER;

                CREATE OR REPLACE FUNCTION get_tenant() RETURNS uuid AS $$
                BEGIN
                    RETURN NULLIF(current_setting('app.current_tenant_id', true), '')::uuid;
                END;
                $$ LANGUAGE plpgsql STABLE;

                -- Enable RLS on analytics_events
                ALTER TABLE analytics_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE analytics_events FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation_select ON analytics_events
                    FOR SELECT
                    USING (organization_id = get_tenant());

                CREATE POLICY tenant_isolation_insert ON analytics_events
                    FOR INSERT
                    WITH CHECK (organization_id = get_tenant());

                -- Enable RLS on erasure_audit_log
                ALTER TABLE erasure_audit_log ENABLE ROW LEVEL SECURITY;
                ALTER TABLE erasure_audit_log FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation_select ON erasure_audit_log
                    FOR SELECT
                    USING (organization_id = get_tenant());

                CREATE POLICY tenant_isolation_insert ON erasure_audit_log
                    FOR INSERT
                    WITH CHECK (organization_id = get_tenant());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation_select ON erasure_audit_log;
                DROP POLICY IF EXISTS tenant_isolation_insert ON erasure_audit_log;
                ALTER TABLE erasure_audit_log DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation_select ON analytics_events;
                DROP POLICY IF EXISTS tenant_isolation_insert ON analytics_events;
                ALTER TABLE analytics_events DISABLE ROW LEVEL SECURITY;

                DROP FUNCTION IF EXISTS get_tenant();
                DROP FUNCTION IF EXISTS set_tenant(uuid);
                """);

            migrationBuilder.DropTable(
                name: "analytics_events");

            migrationBuilder.DropTable(
                name: "erasure_audit_log");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
