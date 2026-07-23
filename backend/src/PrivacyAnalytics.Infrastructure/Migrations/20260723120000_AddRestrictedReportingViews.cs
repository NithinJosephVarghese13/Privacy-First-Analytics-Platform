using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrivacyAnalytics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRestrictedReportingViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- View 1: Event-level restricted reporting view.
                -- Exposes only aggregate-safe columns for AI querying (no raw hashes/PII fields).
                -- Uses WITH (security_invoker = true) so PostgreSQL enforces the underlying table's
                -- Row-Level Security policy (organization_id = current_setting('app.current_tenant_id', true)::uuid).
                CREATE OR REPLACE VIEW reporting_analytics_events WITH (security_invoker = true) AS
                SELECT 
                    event_id,
                    organization_id,
                    timestamp,
                    event_type,
                    path,
                    is_authenticated
                FROM analytics_events;

                -- View 2: Aggregated daily pageviews reporting view.
                CREATE OR REPLACE VIEW reporting_daily_pageviews WITH (security_invoker = true) AS
                SELECT 
                    organization_id,
                    date_trunc('day', timestamp) AS event_date,
                    path,
                    event_type,
                    COUNT(*) AS total_events
                FROM analytics_events
                GROUP BY organization_id, date_trunc('day', timestamp), path, event_type;

                -- View 3: Aggregated top pages reporting view.
                CREATE OR REPLACE VIEW reporting_top_pages WITH (security_invoker = true) AS
                SELECT 
                    organization_id,
                    path,
                    event_type,
                    COUNT(*) AS total_events
                FROM analytics_events
                GROUP BY organization_id, path, event_type;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS reporting_top_pages;
                DROP VIEW IF EXISTS reporting_daily_pageviews;
                DROP VIEW IF EXISTS reporting_analytics_events;
                """);
        }
    }
}
