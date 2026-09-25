using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase45AuditImmutability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!ActiveProvider.Contains("Npgsql"))
                return;

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION icehott_reject_tool_audit_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'tool audit rows are append-only'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER tr_tool_execution_audit_events_append_only
                BEFORE UPDATE OR DELETE ON tool_execution_audit_events
                FOR EACH ROW EXECUTE FUNCTION icehott_reject_tool_audit_mutation();

                CREATE TRIGGER tr_tool_policy_audit_events_append_only
                BEFORE UPDATE OR DELETE ON tool_policy_audit_events
                FOR EACH ROW EXECUTE FUNCTION icehott_reject_tool_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!ActiveProvider.Contains("Npgsql"))
                return;

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_tool_execution_audit_events_append_only
                    ON tool_execution_audit_events;
                DROP TRIGGER IF EXISTS tr_tool_policy_audit_events_append_only
                    ON tool_policy_audit_events;
                DROP FUNCTION IF EXISTS icehott_reject_tool_audit_mutation();
                """);
        }
    }
}
