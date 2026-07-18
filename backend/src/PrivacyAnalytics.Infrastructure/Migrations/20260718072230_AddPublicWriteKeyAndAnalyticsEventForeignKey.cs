using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrivacyAnalytics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicWriteKeyAndAnalyticsEventForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "public_write_key",
                table: "organizations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_organizations_public_write_key",
                table: "organizations",
                column: "public_write_key",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_analytics_events_organization_id",
                table: "analytics_events",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "org_id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_analytics_events_organization_id",
                table: "analytics_events");

            migrationBuilder.DropIndex(
                name: "IX_organizations_public_write_key",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "public_write_key",
                table: "organizations");
        }
    }
}
