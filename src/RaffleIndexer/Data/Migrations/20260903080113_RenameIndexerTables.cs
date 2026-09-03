using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RaffleIndexer.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameIndexerTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_metadata",
                table: "metadata");

            migrationBuilder.DropPrimaryKey(
                name: "pk_cursor",
                table: "cursor");

            migrationBuilder.RenameTable(
                name: "metadata",
                newName: "indexer_metadata");

            migrationBuilder.RenameTable(
                name: "cursor",
                newName: "indexer_cursor");

            migrationBuilder.AddPrimaryKey(
                name: "pk_indexer_metadata",
                table: "indexer_metadata",
                column: "key");

            migrationBuilder.AddPrimaryKey(
                name: "pk_indexer_cursor",
                table: "indexer_cursor",
                column: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_indexer_metadata",
                table: "indexer_metadata");

            migrationBuilder.DropPrimaryKey(
                name: "pk_indexer_cursor",
                table: "indexer_cursor");

            migrationBuilder.RenameTable(
                name: "indexer_metadata",
                newName: "metadata");

            migrationBuilder.RenameTable(
                name: "indexer_cursor",
                newName: "cursor");

            migrationBuilder.AddPrimaryKey(
                name: "pk_metadata",
                table: "metadata",
                column: "key");

            migrationBuilder.AddPrimaryKey(
                name: "pk_cursor",
                table: "cursor",
                column: "id");
        }
    }
}
