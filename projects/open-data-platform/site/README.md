# Commercial data-product site

This directory is deliberately a **zero-build static launch surface** for the first database product.

It is not an ecommerce application and must not grow customer accounts, payment processing, entitlement state, telemetry, or a hosted query service merely to sell a downloadable database.

## Public brand

The launch surface is intentionally **brand-neutral** for now.

`Databasis` may remain an internal/project working name, but it must not be treated as a cleared public brand. Current external checks found active commercial use of the name and an already-registered `databasis.io` domain. A public company/product-family name should be selected and cleared before production deployment, checkout, invoices, SEO, or marketplace listings are tied to it.

This naming decision does not block product-led buyer validation for `Global Research Organization History`.

## Current pages

```text
site/
├── index.html
├── methodology.html
├── styles.css
└── products/
    └── global-research-organization-history.html
```

Open `index.html` directly in a browser to review the site locally. No package manager, build step, JavaScript runtime, or external asset pipeline is required.

## Commercial gate

The current ROR product page intentionally exposes **no live purchase link**.

Do not replace the disabled purchase state until all of the following are true:

1. issue #190 has a genuine human-reviewed exact customer-terms document;
2. the repository approval artifact verifies for `prod_global_research_organization_history`;
3. `commercial_readiness.py status` reports `TECHNICALLY_READY_FOR_SALE` for the exact snapshot and approval;
4. `commercial_readiness.py bind` has created and verified the immutable sale envelope for the exact deliverable;
5. the Merchant-of-Record product is configured to deliver that exact approved commercial artifact.

The website must never infer sale readiness merely because CI is green or a payment product exists.

## Merchant of Record

Launch choice: **Lemon Squeezy first**, with Paddle retained as a fallback.

Before wiring a live checkout, obtain provider-policy confirmation that the exact product is eligible. Lemon Squeezy generally supports digitally fulfilled goods, but its acceptable-use rules also reject products where the merchant lacks the required rights and certain resale-style products. Our product must be described accurately as an independently produced B2B database built from CC0 source data, with paid value in historical capture, semantic changes, quality/provenance, schemas, integrity and packaging—not as a resale of proprietary third-party content.

If Lemon Squeezy declines the product under its own provider policy, evaluate a different Merchant of Record rather than changing the source-rights model or making an inaccurate rights claim.

The initial intended external setup is intentionally small:

1. ask the provider to pre-clear the exact CC0-derived product description;
2. create and activate the business/store account through the provider's required verification process;
3. create one product for the `INTERNAL_COMMERCIAL` ROR offer;
4. use one-time pricing initially;
5. upload/deliver only the exact commercial artifact permitted by the sale-envelope process;
6. put the real hosted checkout URL into the site only after the commercial gate above passes.

Do not implement a second billing or download-entitlement system in this repository unless a proven requirement appears.

## Price

The current GTM hypothesis is to validate a one-time internal-commercial price roughly in the **€490–€990** range against real buyer conversations, with **€790** as the first conversation anchor.

This range is not a public price commitment. The static site deliberately omits a price until the commercial terms are approved and the hypothesis has enough customer evidence to choose an actual launch price.

## Sample data

No synthetic or invented sample is published merely to fill a marketing slot.

A public sample should be added only when it can be generated deterministically from a verified real product state, with a clear source-rights/provenance notice and a stable schema relationship to the paid product.

## First customer profile

Optimize the copy for commercial scholarly-publishing, research-information, research-integrity, and metadata software teams that already depend on ROR and repeatedly maintain ingestion/history/normalization logic.

Do not broaden V1 into generic university procurement language until actual demand shows that universities are a better direct buyer.

Canonical GTM tracking: issue #196.

## Distribution order

1. own static website + Merchant-of-Record checkout;
2. direct B2B outreach;
3. AWS Data Exchange after the own-site sale path works;
4. Snowflake Marketplace only when demand justifies a Snowflake-native distribution;
5. Databricks Marketplace only when demand justifies that integration.

The marketplaces are additional discovery/distribution channels, not a replacement for the canonical product/release process.
