namespace SelfService.Foundation.Tests;

// Synthetic fixtures only. Production upgrades never drop these retained authority/evidence tables.
internal static class FoundationTestSchemaDowngrades
{
    internal static void DropCredentialTablesAfter(StoreFixture fixture, int version)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (version is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(version));
        fixture.Execute("""
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
