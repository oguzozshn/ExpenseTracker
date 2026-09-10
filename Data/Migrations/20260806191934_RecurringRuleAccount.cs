using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecurringRuleAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "RecurringRules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringRules_AccountId",
                table: "RecurringRules",
                column: "AccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringRules_Accounts_AccountId",
                table: "RecurringRules",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RecurringRules_Accounts_AccountId",
                table: "RecurringRules");

            migrationBuilder.DropIndex(
                name: "IX_RecurringRules_AccountId",
                table: "RecurringRules");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "RecurringRules");
        }
    }
}
