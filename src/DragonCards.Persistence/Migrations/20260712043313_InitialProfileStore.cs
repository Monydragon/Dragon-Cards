using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DragonCards.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialProfileStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 18, nullable: false, collation: "NOCASE"),
                    CreatedUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPlayedUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Experience = table.Column<int>(type: "INTEGER", nullable: false),
                    Coins = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultRulesJson = table.Column<string>(type: "TEXT", nullable: false),
                    SelectedStarterDeckId = table.Column<string>(type: "TEXT", nullable: false),
                    ActiveDeckId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CardCopies",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CardId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Copies = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardCopies", x => new { x.ProfileId, x.CardId });
                    table.ForeignKey(
                        name: "FK_CardCopies_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Decks",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DeckId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ModeId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    UpdatedUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Decks", x => new { x.ProfileId, x.DeckId });
                    table.ForeignKey(
                        name: "FK_Decks_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackInventory",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PackId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackInventory", x => new { x.ProfileId, x.PackId });
                    table.ForeignKey(
                        name: "FK_PackInventory_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OccurredUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileEvents_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestEntries",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    QuestId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Progress = table.Column<int>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestEntries", x => new { x.ProfileId, x.QuestId });
                    table.ForeignKey(
                        name: "FK_QuestEntries_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestStates",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DailyPeriod = table.Column<string>(type: "TEXT", nullable: false),
                    WeeklyPeriod = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestStates", x => x.ProfileId);
                    table.ForeignKey(
                        name: "FK_QuestStates_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StarterDeckOwnership",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StarterDeckId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StarterDeckOwnership", x => new { x.ProfileId, x.StarterDeckId });
                    table.ForeignKey(
                        name: "FK_StarterDeckOwnership_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TutorialCompletions",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TutorialId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CompletedUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TutorialCompletions", x => new { x.ProfileId, x.TutorialId });
                    table.ForeignKey(
                        name: "FK_TutorialCompletions_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeckCards",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DeckId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CardId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Copies = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckCards", x => new { x.ProfileId, x.DeckId, x.CardId });
                    table.ForeignKey(
                        name: "FK_DeckCards_Decks_ProfileId_DeckId",
                        columns: x => new { x.ProfileId, x.DeckId },
                        principalTable: "Decks",
                        principalColumns: new[] { "ProfileId", "DeckId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Decks_ProfileId_Name",
                table: "Decks",
                columns: new[] { "ProfileId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileEvents_ProfileId_OccurredUnixMilliseconds",
                table: "ProfileEvents",
                columns: new[] { "ProfileId", "OccurredUnixMilliseconds" });

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_DisplayName",
                table: "Profiles",
                column: "DisplayName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "CardCopies");

            migrationBuilder.DropTable(
                name: "DeckCards");

            migrationBuilder.DropTable(
                name: "PackInventory");

            migrationBuilder.DropTable(
                name: "ProfileEvents");

            migrationBuilder.DropTable(
                name: "QuestEntries");

            migrationBuilder.DropTable(
                name: "QuestStates");

            migrationBuilder.DropTable(
                name: "StarterDeckOwnership");

            migrationBuilder.DropTable(
                name: "TutorialCompletions");

            migrationBuilder.DropTable(
                name: "Decks");

            migrationBuilder.DropTable(
                name: "Profiles");
        }
    }
}
