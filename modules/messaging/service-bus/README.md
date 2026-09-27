# Private Premium Service Bus

Owns one Premium namespace, network rules, work queue, namespace-scoped worker Data Owner and queue-scoped sender grants, plus diagnostics. Local/SAS authentication and public access are disabled. The Microsoft Functions identity-trigger guidance requires the Data Owner role or a qualified custom role including namespace reads; this single-purpose namespace limits that grant. Custom-role reduction requires separate runtime validation. Queue TTL fourteen days, max delivery ten and duplicate window ten minutes. References workspace and principal IDs; outputs id/name. Private endpoint belongs to the composition.

See [module source](main.bicep) for the exact interface and [product onboarding](../../../docs/workload-onboarding.md) for composition, tests and acceptance. Local modules are compiled into versioned Template Specs; no registry is required. No module independently grants deployment authorization.
