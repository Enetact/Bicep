namespace SelfService.Foundation.Tests;

// Synthetic fixtures only. Production upgrades never drop these retained authority/evidence tables.
internal static class FoundationTestSchemaDowngrades
{
    internal static void DropCredentialTablesAfter(StoreFixture fixture, int version)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (version is < 1 or > 11) throw new ArgumentOutOfRangeException(nameof(version));
        fixture.Execute("""
            DROP TABLE IF EXISTS credential_runtime_bindings;
            """);
        if (version < 11) fixture.Execute("""
            DROP TABLE IF EXISTS recovery_execution_holds;
            """);
        if (version < 10) fixture.Execute("""
            DROP TABLE IF EXISTS credential_scoped_recovery_joint_executions;
            """);
        if (version < 9) fixture.Execute("""
            DROP TABLE IF EXISTS credential_scoped_recovery_stage_review_executions;
            DROP TABLE IF EXISTS credential_scoped_recovery_stage_review_approvals;
            DROP TABLE IF EXISTS credential_scoped_recovery_stage_review_requests;
            """);
        if (version < 8) fixture.Execute("""
            DROP TABLE IF EXISTS credential_recovery_stage_review_executions;
            DROP TABLE IF EXISTS credential_recovery_stage_review_approvals;
            DROP TABLE IF EXISTS credential_recovery_stage_review_requests;
            """);
        if (version < 7) fixture.Execute("""
            DROP TABLE IF EXISTS credential_maintenance_observation_receipts;
            DROP TABLE IF EXISTS credential_maintenance_observation_approvals;
            DROP TABLE IF EXISTS credential_maintenance_observation_requests;
            """);
        if (version < 6)
            fixture.Execute("""
                DROP TABLE IF EXISTS credential_maintenance_executions;
                DROP TABLE IF EXISTS credential_maintenance_approvals;
                DROP TABLE IF EXISTS credential_maintenance_intents;
                """);
    }
}
