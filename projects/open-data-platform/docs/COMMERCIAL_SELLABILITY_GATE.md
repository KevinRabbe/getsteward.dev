# Commercial sellability gate

## Objective

Move from a technically complete `PRODUCT_CANDIDATE` to an immutable **technical sale-readiness binding** without allowing the software to invent, approve, or silently mutate legal terms.

The gate is intentionally conservative:

```text
commercial candidate
+ operational acceptance
+ human-reviewed customer-terms artifact
+ exact hash binding
= TECHNICALLY_READY_FOR_SALE
```

`TECHNICALLY_READY_FOR_SALE` is a technical state. It is **not** an independent legal opinion and does not prove that a reviewer is legally qualified.

## Four separate states

The platform keeps these concepts distinct:

```text
1. PRODUCT_CANDIDATE
   Product bytes, history, quality and rights notices are technically complete.

2. Operationally accepted
   Required real production / source evidence passed.

3. Human legal approval artifact present
   A human reviewer approved one exact customer-terms document and produced the required approval record.

4. TECHNICALLY_READY_FOR_SALE
   The platform cryptographically binds the exact candidate, exact operational evidence and exact approved terms document.
```

No earlier state automatically implies a later one.

## Legal-review handoff

Generate the factual handoff for one product:

```powershell
python .\commercial_readiness.py packet prod_global_legal_entity_history
python .\commercial_readiness.py packet prod_global_research_organization_history
```

The packet contains:

- product/source-rights facts already locked by the product catalog;
- excluded-field policy where applicable;
- technical non-negotiables;
- decisions that must be made by a human legal reviewer;
- the required machine-readable approval-record contract.

It deliberately does not draft the final customer terms.

## Technical non-negotiables

Customer terms must preserve the architecture/business boundaries already chosen for these products:

```text
no exclusive claim over underlying source facts/identifiers
source-data rights remain separate from vendor-added value
required non-affiliation position remains intact
no usage metering requirement
no telemetry requirement
no revenue-share requirement
no recurring usage-report requirement
no customer-data custody obligation
no responsibility for customers' downstream products
```

For ROR, the existing product exclusion also remains mandatory:

```text
excluded_source_fields = ["locations"]
```

A separately reviewed future product may change that rights boundary. The current sellability gate cannot broaden it.

## Deliberately invalid template

The repository includes:

```text
catalog/legal/customer_terms_approval.example.json
```

It is deliberately marked:

```text
status = NOT_APPROVED_TEMPLATE
```

and the platform rejects it.

It exists only to show the fields a real approval record must contain. Changing the status string alone is insufficient: the exact reviewed document hash and all required invariants are verified.

## Approval artifact

A real human-review record must live beside the exact reviewed terms document.

Conceptually:

```json
{
  "approval_record_version": 1,
  "status": "APPROVED",
  "terms_id": "internal-commercial-v1",
  "license_plan": "INTERNAL_COMMERCIAL",
  "applies_to_product_ids": ["prod_global_research_organization_history"],
  "approved_at": "2026-07-27T12:00:00+02:00",
  "review_reference": "external-review-reference",
  "terms_document_file": "customer-terms-v1.pdf",
  "terms_document_sha256": "<64 lowercase hex characters>",
  "source_rights_preserved": true,
  "no_exclusive_source_data_claim": true,
  "non_affiliation_preserved": true,
  "usage_metering_required": false,
  "revenue_share_required": false,
  "telemetry_required": false
}
```

The platform verifies:

- supported approval schema/version;
- exact product applicability;
- supported licence plan;
- timezone-aware approval timestamp;
- non-empty human review reference;
- exact terms-document SHA-256;
- required true/false technical invariants;
- safe local file names / no path traversal;
- safe stable `terms_id`.

It does **not** verify the substantive quality of the legal wording or the qualifications of the reviewer.

## Approval verification

```powershell
python .\commercial_readiness.py verify-approval .\legal\approval.json `
  --product-id prod_global_research_organization_history
```

A changed terms document immediately invalidates the approval hash.

An approval for one product cannot be silently reused for a product that is absent from `applies_to_product_ids`.

## Operational gates

Operational acceptance remains product-specific.

### Global Legal Entity History

The GLEIF candidate remains blocked until the existing Phase 17 production gate reports:

```text
7 distinct successful real GLEIF Level-1 source versions
```

Repeating one source version does not advance the gate.

Therefore an approved customer-terms artifact cannot bypass an incomplete real production window.

### Global Research Organization History

ROR uses the committed real-source acceptance evidence:

```text
docs/live-acceptance/ror-v2.9-v2.10.json
```

The gate requires the real acceptance wrapper and inner evidence to be `PASS`, the verified historical comparison to match the published updated-existing evidence, and at least one real adjacent history pair.

Crucially, the live acceptance's commercial candidate bundle SHA-256 must equal the **exact current ROR candidate bundle SHA-256** being considered for sale.

A real PASS for one candidate cannot authorize a different rebuilt candidate.

## Readiness status

Inspect without mutation:

```powershell
python .\commercial_readiness.py status `
  prod_global_research_organization_history `
  <snapshot_id>
```

Optionally include an approval artifact:

```powershell
python .\commercial_readiness.py status `
  prod_global_research_organization_history `
  <snapshot_id> `
  --terms-approval .\legal\approval.json
```

Possible states:

```text
BLOCKED_LEGAL_AND_OPERATIONAL
BLOCKED_LEGAL_REVIEW
BLOCKED_OPERATIONAL_ACCEPTANCE
TECHNICALLY_READY_FOR_SALE
```

The commercial candidate itself remains `PRODUCT_CANDIDATE` and `LEGAL_REVIEW_REQUIRED`; readiness is represented by a separate binding rather than mutating historical product metadata.

## Immutable sale envelope

When every technical gate passes:

```powershell
python .\commercial_readiness.py bind `
  prod_global_research_organization_history `
  <snapshot_id> `
  --terms-approval .\legal\approval.json
```

The runtime envelope is stored under:

```text
data/sale-envelopes/<product_id>/<snapshot_id>/<terms_id>/
```

and contains:

```text
sale.json
sale.json.sha256
approval.json
approval.json.sha256
<exact reviewed terms document>
<exact reviewed terms document>.sha256
operational.json
operational.json.sha256
```

`data/sale-envelopes/` is runtime/business evidence and is excluded from Git.

## What the envelope binds

`sale.json` binds:

- exact product ID and snapshot;
- exact commercial candidate manifest SHA-256;
- exact commercial bundle SHA-256;
- exact `terms_id` and licence plan;
- exact approval-record SHA-256;
- exact reviewed terms-document SHA-256;
- exact operational-evidence SHA-256;
- source-rights preservation invariant;
- no-exclusive-source-data-claim invariant;
- non-affiliation invariant;
- no-metering / no-revenue-share / no-telemetry invariants.

This gives the sales/delivery process one stable technical object to verify rather than combining candidate, legal file and operational proof manually each time.

## Envelope verification

```powershell
python .\commercial_readiness.py verify-envelope `
  prod_global_research_organization_history `
  <snapshot_id> `
  internal-commercial-v1
```

Verification re-checks:

- current immutable candidate hashes;
- approval artifact and terms document;
- approval/document checksum sidecars;
- stored operational evidence;
- product-specific operational invariants;
- all sale-envelope invariants.

For ROR, stored operational evidence must remain bound to the same commercial candidate bundle SHA.

An existing envelope is never overwritten. A repeated bind verifies it and returns `NO_CHANGE`.

## Security / trust boundary

The envelope uses SHA-256 fixity, not a public-key signature system.

It is designed to prevent accidental substitution, drift, wrong-version delivery and silent mismatch inside the platform's existing integrity model.

It is not a substitute for external document-signing infrastructure, qualified electronic signatures, legal review, contract execution, payment/entitlement records, or audit controls that a later business process may require.

## What is intentionally still missing

This slice does not add:

- approved customer terms;
- legal advice;
- reviewer qualification checks;
- payment processing;
- entitlement/customer accounts;
- telemetry or usage tracking;
- revenue share;
- customer-specific product builds;
- hosted SLA/API obligations.

The next irreversible external step is intentionally human: obtain one real legal review of the customer terms. Until then, the software should continue to say `BLOCKED_LEGAL_REVIEW` even when every technical product and operational gate is green.
