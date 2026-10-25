# Microsoft Learn decisions

Provider id: `learn`. This public documentation connection requires no Azure/ADO token. Its success does not validate any cloud account, tenant, subscription or service permission.

Use `microsoft_docs_search` to locate relevant product documentation, `microsoft_docs_fetch` to inspect a specific source page, and `microsoft_code_sample_search` for implementation examples. Discover the current argument schemas first; do not invent search/filter parameter names. Keep queries specific to product/service, task, language/SDK and relevant version.

Start with focused search, then fetch the most relevant authoritative page to resolve a disputed requirement or implementation detail. Check that samples and instructions match the requested product, API/SDK version, hosting model and deployment scope. Distinguish generally available features from preview behavior, and avoid carrying a pattern from one Functions/identity/networking model into another without evidence.

Cite the returned source URL beside the claim it supports. Use short necessary excerpts and explain the application-specific inference separately. A documentation recommendation is not proof that the current tenant enables the feature or that this repository implements it. Pair research with actual provider evidence when describing the user's setup.

Send only the minimum generic technical context to public search; omit private resource names, tenant/account identifiers, credentials and raw logs. For a missing result, refine the query or report the evidence gap; do not fabricate a citation or claim that no service capability exists. Documentation and samples inform an ordered proposal; fetching them never authorizes executing their commands.
