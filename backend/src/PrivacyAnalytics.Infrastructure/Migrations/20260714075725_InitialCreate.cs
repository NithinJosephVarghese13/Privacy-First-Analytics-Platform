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
                -- Convert analytics_events to a TimescaleDB hypertable partitioned on timestamp.
                SELECT create_hypertable('analytics_events', 'timestamp',
                    if_not_exists => TRUE,
                    migrate_data => TRUE);

                -- Enable Row-Level Security on analytics_events and FORCE it so that even
                -- the table owner is subject to the policy (no silent owner bypass).
                ALTER TABLE analytics_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE analytics_events FORCE ROW LEVEL SECURITY;

                -- Fail-closed tenant isolation. current_setting('app.current_tenant_id', true)
                -- returns NULL when the session variable was never set, and '' after a RESET (the
                -- default for placeholder GUCs). NULLIF collapses the empty-string case to NULL too,
                -- so the cast never raises. NULL::uuid is NULL, therefore
                -- `organization_id = NULL` evaluates to NULL (not TRUE) and zero rows are returned.
                -- A missing tenant context yields an empty result set, never an error that could be
                -- swallowed upstream.
                CREATE POLICY tenant_isolation_select ON analytics_events
                    FOR SELECT
                    USING (organization_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

                CREATE POLICY tenant_isolation_insert ON analytics_events
                    FOR INSERT
                    WITH CHECK (organization_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

                -- Apply the same fail-closed isolation to the insert-only erasure audit log.
                ALTER TABLE erasure_audit_log ENABLE ROW LEVEL SECURITY;
                ALTER TABLE erasure_audit_log FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation_select ON erasure_audit_log
                    FOR SELECT
                    USING (organization_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

                CREATE POLICY tenant_isolation_insert ON erasure_audit_log
                    FOR INSERT
                    WITH CHECK (organization_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
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
