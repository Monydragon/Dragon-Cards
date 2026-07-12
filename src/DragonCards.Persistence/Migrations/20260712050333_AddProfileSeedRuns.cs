using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DragonCards.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileSeedRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProfileSeedRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    AlgorithmVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Scenario = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AppliedUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSeedRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileSeedRuns_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSeedRuns_ProfileId_AppliedUnixMilliseconds",
                table: "ProfileSeedRuns",
                columns: new[] { "ProfileId", "AppliedUnixMilliseconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileSeedRuns");
        }
    }
}
