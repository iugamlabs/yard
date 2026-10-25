using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YardApp.Api.Infrastructure.Migrations {
    /// <inheritdoc />
    public partial class InitialNotes : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "Notes",
                columns: table => new {
                    Id = table
                        .Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Text = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                },
                constraints: table => {
                    table.PrimaryKey("PK_Notes", x => x.Id);
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(name: "Notes");
        }
    }
}
