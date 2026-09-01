using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RaffleIndexer.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cursor",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    last_indexed_block = table.Column<long>(type: "bigint", nullable: false),
                    chain_head_block = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cursor", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metadata",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadata", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "rounds",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    opened_at_block = table.Column<long>(type: "bigint", nullable: false),
                    opened_at_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_at_block = table.Column<long>(type: "bigint", nullable: true),
                    request_id = table.Column<BigInteger>(type: "numeric(78,0)", nullable: true),
                    settled_at_block = table.Column<long>(type: "bigint", nullable: true),
                    settled_at_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    winner_address = table.Column<string>(type: "text", nullable: true),
                    prize_wei = table.Column<BigInteger>(type: "numeric(78,0)", nullable: true),
                    entry_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rounds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    round_id = table.Column<int>(type: "integer", nullable: false),
                    player_address = table.Column<string>(type: "text", nullable: false),
                    block_number = table.Column<long>(type: "bigint", nullable: false),
                    block_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tx_hash = table.Column<string>(type: "text", nullable: false),
                    log_index = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_entries_rounds_round_id",
                        column: x => x.round_id,
                        principalTable: "rounds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_entries_player_address",
                table: "entries",
                column: "player_address");

            migrationBuilder.CreateIndex(
                name: "ix_entries_round_id",
                table: "entries",
                column: "round_id");

            migrationBuilder.CreateIndex(
                name: "ix_entries_tx_hash_log_index",
                table: "entries",
                columns: new[] { "tx_hash", "log_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rounds_status",
                table: "rounds",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_rounds_winner_address",
                table: "rounds",
                column: "winner_address");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cursor");

            migrationBuilder.DropTable(
                name: "entries");

            migrationBuilder.DropTable(
                name: "metadata");

            migrationBuilder.DropTable(
                name: "rounds");
        }
    }
}
