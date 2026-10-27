using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CmsEventService.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Deletions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    TimestampTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deletions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Entities",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    TimestampTicks = table.Column<long>(type: "bigint", nullable: false),
                    Published = table.Column<bool>(type: "boolean", nullable: false),
                    LocallyDisabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entities", x => x.Id);
                    table.CheckConstraint("CK_Entity_Version", "\"Version\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Entities_Published_LocallyDisabled_Id",
                table: "Entities",
                columns: new[] { "Published", "LocallyDisabled", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Deletions");

            migrationBuilder.DropTable(
                name: "Entities");
        }
    }
}
