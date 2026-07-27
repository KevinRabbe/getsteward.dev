# Customer-terms legal review artifacts

This directory contains the **technical handoff contract** for customer-terms approval. It does not contain approved legal terms.

`customer_terms_approval.example.json` is intentionally invalid for sale:

```text
status = NOT_APPROVED_TEMPLATE
```

The Open Data Platform will reject it.

A real approval record belongs beside the exact human-reviewed terms document and must reference that document by SHA-256. The platform verifies only the artifact structure, hashes, product applicability, and required technical invariants.

It does **not** determine whether the wording is legally sufficient and does not determine whether a reviewer is legally qualified.

Approved terms are runtime/business records and should not be committed here merely to make tests pass.
