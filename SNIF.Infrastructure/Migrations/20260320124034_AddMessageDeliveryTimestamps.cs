using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SNIF.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageDeliveryTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ReceiverId",
                table: "Messages");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReadAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ReceiverId_DeliveredAt",
                table: "Messages",
                columns: new[] { "ReceiverId", "DeliveredAt" },
                filter: "\"DeliveredAt\" IS NULL");

            // Backfill: set ReadAt and DeliveredAt for already-read messages
            migrationBuilder.Sql(
                "UPDATE \"Messages\" SET \"ReadAt\" = \"UpdatedAt\", \"DeliveredAt\" = \"UpdatedAt\" WHERE \"IsRead\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ReceiverId_DeliveredAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "DeliveredAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReadAt",
                table: "Messages");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ReceiverId",
                table: "Messages",
                column: "ReceiverId");
        }
    }
}
