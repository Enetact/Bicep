# Run-menu costs and deployment options

Logic App/Event Grid costs: the new `eventflow` profile uses WS1 plus eight private endpoints, approximately USD 233.56/month fixed subtotal in the dated East US 2 snapshot, with usage/additional costs excluded. The resolved Event flow estimate also adds fixed charges for owned DNS zones; queries and log/storage usage remain additional. See the [workload cost assumptions and evidence](../workloads/logic-app-event-grid/README.md#costs-and-local-tests). The existing calculations below describe blob-transfer.

The Deploy menu shows reference prices before a run. A selected estimate is saved after input validation during Preview and again in the qualified bundle; early onboarding failure can occur before an estimate is written. Discover only collects inventory; cost fields are shown in Deploy; endpoint and alert settings are now platform-owned in `config/platform.json`. The source is Microsoft's public [Azure Retail Prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices), captured in `self-service/pricing/usd-eastus2.json` with complete meter records and a retrieval timestamp. These are USD pay-as-you-go retail estimates for East US 2, not a subscription quote or spending limit. No billing-account access is needed to refresh them.

## Current reference amounts

Snapshot retrieved **19 September 2026 UTC** (18 September US Central). Month = **730 hours**. All amounts below exclude tax, negotiated discounts, reservations, credits and usage charges.

| Component | Monthly fixed estimate |
|---|---:|
| Linux B1 hosting, each instance | $12.41 |
| Linux S1 hosting, each instance | $69.35 |
| Linux P1v3 hosting, each instance | $113.15 |
| Six required private endpoints: five storage + Function App | $43.80 |
| Five new private DNS zones, first-25-zones tier | $2.50 |
| Optional destination blob endpoint | $7.30 |
| Optional destination dfs endpoint, when HNS-enabled | $7.30 |
| Three enabled five-minute log alerts | $4.50 |

For the checked-in dev profile, new networking, HNS destination and both platform options enabled: **$77.81/month fixed subtotal plus usage**. Without new destination endpoints and with alerts disabled: **$58.71/month plus usage and any reused destination-endpoint charges**. This is a full Release footprint, not a Foundation-only estimate, not proof that the profile is enabled/ready, and not the incremental change to an existing bill. Existing shared DNS/endpoint resources remain billable to their owner.

Storage capacity and transactions, Private Link data processing, bandwidth/egress, retained blob versions, DNS queries, log ingestion/retention, alert notifications, the existing destination and the private agent are excluded from the fixed subtotal. The snapshot's paid log-ingestion tier is $2.76/GB; DNS queries are $0.40/million. Allowances depend on subscription usage. The log daily cap is not a total Azure spending cap. See [App Service](https://azure.microsoft.com/en-us/pricing/details/app-service/linux/), [Private Link](https://azure.microsoft.com/en-us/pricing/details/private-link/), [DNS](https://azure.microsoft.com/en-us/pricing/details/dns/), and [Monitor](https://azure.microsoft.com/en-us/pricing/details/monitor/) pricing for billing rules.

## Platform option behavior

- **Create destination private endpoints**, platform default `true`: maps to `createDestinationPrivateEndpoints`. Blob is created; dfs is added only for an HNS destination. Setting false requires pre-existing private routes and DNS. Runtime readiness and smoke checks still apply; this never enables public storage access. Stack self-service rejects removing already managed endpoints; the legacy incremental path retains them. Changing this option alone does not guarantee savings.
- **Enable log alerts**, platform default `true`: maps to `enableLogAlerts`. Disabling keeps all three rules but sets their `enabled` properties to false; log ingestion stays on. Production rejects false in both early pipeline validation and parameter/bundle validation. Microsoft lists disabled alert rules as uncharged; any remaining log ingestion is separate.
- Hosting, the Function App, storage/queues/ledger, identity, private app/storage access, and telemetry are required dependencies of this blueprint. There are no pretend switches for these resources. Network creation versus reuse is selected through the registered network profile.
- Choices apply only to deployment. Discovery remains read-only. Values are frozen in `parameters.json` and covered by the bundle hash; later stages use that bundle, not new queue-time overrides.

## Estimate artifact and approval display

`New-SelfServiceBundle.ps1` calculates prices after applying the reviewed profile and platform options. It writes **cost-estimate.json** into `self-service-bundle` and hashes it in `bundle.json`. The file includes quantities, fixed subtotal, currency/region/date, exclusions and selections. A readable cost summary is uploaded to the run's Extensions tab. Foundation/Release preview summaries repeat the options and full-release subtotal for the approver.

An unsupported or implicit region, unsupported SKU, or snapshot older than 30 days produces **Unavailable**, never a misleading zero. The pipeline still requires its usual approval; an estimate is advisory, not an enforcement budget. Developers cannot override these infrastructure options in the current intent menu. The native Run pipeline form displays the checked-in reference rates and date; it cannot fetch live prices or recalculate totals for each selection. Its date must be reviewed when queuing. A custom form would be needed for reactive totals. Shared workspace, hub/resolver and registry costs belong to the platform and are outside this workload subtotal.

## Refresh the price snapshot and menu

From the repository root in PowerShell 7:

```powershell
./scripts/Update-ServicePrices.ps1
./scripts/Update-ServiceCatalog.ps1
./scripts/Update-ServiceCatalog.ps1 -Check
./scripts/Test-ServiceCosts.ps1
./scripts/Update-Manifest.ps1
./scripts/Update-Manifest.ps1 -Check
git diff
```

Review and commit the snapshot, generated pipeline entry points/router and manifest, then push the branch. Open a new Run pipeline dialog on that branch. Refresh performs only public HTTP reads; it does not sign into Azure or deploy. It selects exact product/meter/region/tier/unit records and stops rather than overwriting the snapshot when a meter is missing or ambiguous. Pricing is not fetched during normal catalog generation or pipeline execution, keeping the committed menu reproducible.

Implementation: `Update-ServicePrices.ps1` retrieves retail evidence; `service-cost-common.ps1` computes and formats estimates; `Update-ServiceCatalog.ps1` generates labels and boolean wiring; `Set-ServiceDeploymentOptions` validates and applies choices; `New-SelfServiceBundle.ps1` freezes them; `workloads/blob-transfer/modules/monitoring.bicep` applies alert enabled states. This adds no application NuGet/npm dependency. Live Azure DevOps menu rendering and optional-resource deployment remain unverified until run from the pushed source.
