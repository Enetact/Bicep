# Platform Studio commercial license review draft

**Non-binding review draft, 24 October 2026.** The selected business direction is proprietary binary licensing, with source access sold separately. This records the user's direction. It is not an effective license, accepted agreement, copyright assignment, pricing commitment, distribution authorization or legal clearance. No repository LICENSE, project license declaration or runtime entitlement has been changed by this draft. [Machine-readable decision](../config/commercial-release-policy.draft.json) · [Release gates](plans/platform-studio-commercial-release.md) · [Dependency evidence](foundation-dependency-evidence.md).

## Proposed offering boundary

| Item | Review proposal | Still to decide |
|---|---|---|
| Binary license | A separately ordered right to run the admitted Platform Studio edition for the named customer and allowed environments. Source access is excluded from this order by default. | Seller/buyer legal entities; licensed edition/version; customer/affiliate/MSP scope; subscription versus perpetual term; license metric and price. |
| Source add-on | A separate paid agreement identifying the source revision, deliverables, permitted access and internal modifications. | Price; who may access it; derivative ownership/support; confidentiality; escrow; permitted hosting/redistribution; treatment after expiry. |
| Intellectual property | Proposed default: retain seller-controlled first-party rights and license defined uses. Source delivery does not propose a copyright assignment. | Establish chain of ownership and resolve employee/contractor, prior-employer and third-party contributions before making a grant. Any assignment/exclusive transaction needs its own expressly reviewed terms. |
| Resale/OEM/white label | No such rights are proposed by the ordinary binary or source order. | A separate negotiated agreement, if wanted. |
| Support/maintenance | A separately described support scope tied to admitted versions and hosting patterns. | Hours, response targets, security updates, upgrade window, fees and end-of-support obligations. No production SLA is currently offered. |
| Delivery | Signed, reviewed release artifacts after the relevant release gates. The current unsigned ZIP is LocalPreview with explicit remaining qualification/rights issues. | Binary/source package contents, deployment topology, approved installer/update/repair/uninstall and acceptance criteria. |

These are proposed commercial boundaries, subject to qualified review and an executed agreement. Do not infer rights in third-party dependencies from the proposed first-party business model. For U.S. review context, ownership of a software copy and ownership of copyright are distinct under section 202; applicable jurisdiction and the actual ownership chain still need review. [U.S. Copyright Office, Chapter 2](https://www.copyright.gov/title17/92chap2.html).

## Technical and customer-data boundary

The order should identify the admitted customer-cell, runtime/backend and workflow/pattern scope. Application roles, technical entitlement documents and contractual usage rights are separate. A paid feature never grants Entra consent, Azure RBAC, repository access, pipeline rights or human approval. Customers supply their own admitted GitHub/Azure DevOps and cloud identities/connections; provider fees and service terms remain separate.

The agreement needs explicit treatment of customer data, confidentiality, permitted model/provider processing, telemetry, security incidents, export/recovery and retention/deletion. Current in-process metrics contain fixed aggregates, while durable audit and artifacts remain customer-cell data. Actual production custody, hosting, consent and model/provider acceptance are still open release gates. License expiry must preserve eligible safe administration and exports; it must not automatically delete cloud resources or replay jobs.

## Review checklist before a first-party license declaration

1. Confirm the seller entity and its right to license every first-party component; settle contribution/assignment evidence and source-origin exceptions.
2. Review the actual binary and source inventories independently. Eight first-party project licenses remain undeclared after adding the owner executable. Fourteen packages lack any archive-supplied notice/license text; twenty-three lack their own declared license file, including notice-only packages. Twenty-two exact-commit supplements are verified, while one package lacks a repository commit and two MCP declarations require upstream license-transition review. Choosing a business direction or acquiring text does not clear those rights questions.
3. Assemble complete applicable third-party license texts, copyright/attribution notices and source/documentation obligations for the selected distribution. For example, the MIT terms require retaining copyright and permission notices in covered copies. [MIT license text](https://opensource.org/license/mit).
4. Have qualified review resolve warranty, liability, indemnity, termination, confidentiality, reverse-engineering restrictions and mandatory-law exceptions, export/tax requirements, governing law and dispute provisions. This draft selects none of those terms.
5. Define the binary order and optional source add-on, support/security/privacy documents, release acceptance and actual customer onboarding process.
6. Only after approval, add the reviewed first-party license file/declarations, re-run dependency/source/final-package evidence and satisfy the applicable release gates. Technical issuer keys and entitlement policy do not substitute for that approval.

## Order-form fields for later completion

Seller and buyer legal entities; customer/affiliate scope; edition/version/revision; named environments/hosting; license metric; term and renewal; price/currency/tax; source add-on and permitted users; optional services; support policy/version; security/privacy/data-processing terms; delivery and acceptance; approved exceptions; authorized signatures and effective date. Values remain unfilled until agreed. This document is an internal review artifact and has not been sent to a buyer or published.
