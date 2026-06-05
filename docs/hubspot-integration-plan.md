# HubSpot Integration — Plan & Design

**Status:** Planning (pre-build) · **Updated after the 2026-06-04 product call**
**Companion doc:** `hubspot-concepts-and-architecture.md` (diagrams)

The single source of truth for what we're building, the decisions made, the questions
still open, and the build order. HubSpot-specific terms are in the glossary (§13).

> **This is a living document — extend it directly.** New open questions go in §11 in the
> format used there: **the question**, why it matters, and a concrete worked example
> (use the running cast: *Sara* from Bayut, *Omar* organic/authenticated, *Layla* anonymous).

---

## 0. Scope reframe from the 2026-06-04 call (read first)

The call narrowed HubSpot's role significantly:

- **HubSpot = top-of-funnel only** — lead capture, nurturing, and retargeting. Once a
  user wants to proceed with a mortgage, everything falls back to **PRYPCO Pulse**,
  where processing happens.
- **PRYPCO Pulse = the processing system of record** (application, documents,
  underwriting, offers, bank products). Where this doc previously said "processing
  portal," read **Pulse**.
- **HubSpot stays light on ops.** The ops surface currently being built in HubSpot is
  **throwaway** — it goes away once Pulse is ready.
- **Bank products / offers are NOT modelled in HubSpot.** We maintain products in our
  own system; we will not keep HubSpot in sync with product/rate changes. The native
  HubSpot **Product** object was reviewed with Lucas and ruled out.
- The **offer** is captured as a **human-readable attribute** (bank name + rate, etc.) —
  enough context for nurturing, no catalogue to sync.

Consequence: the granular application lifecycle leaves HubSpot. The HubSpot deal
pipeline shrinks to the top-of-funnel stages plus a coarse outcome; the detailed
application stages live in Pulse.

---

## 1. Overview

Leads arrive from Bayut/Dubizzle (and organic channels) via our customer portal and sync
into HubSpot for top-of-funnel nurturing and qualification. When a user decides to
proceed, they hand off to **PRYPCO Pulse**, the system of record for the application and
all downstream processing. Our shared backend is the integration hub between the portal,
HubSpot, and Pulse.

---

## 2. Systems & responsibilities

| System | Role | System of record for |
|---|---|---|
| **Customer portal** (B2C) | Lead capture (Bayut/Dubizzle + organic) | Customer-submitted identity & inquiry data |
| **Shared backend + DB** | Integration hub | Linking keys + stored HubSpot/Pulse ids; orchestration |
| **HubSpot CRM** (Pro) | Top-of-funnel: nurturing, qualification, retargeting | Funnel/qualification state + nurturing engagement |
| **PRYPCO Pulse** | Application & processing | Application, documents, underwriting, offers, products |

`Note: the current HubSpot ops build is interim/throwaway and will be replaced by Pulse.`

The organising principle (§6): every field flows one way, outward from its owner.

---

## 3. HubSpot data model

Salesforce → HubSpot mapping:

| Salesforce | HubSpot | Note |
|---|---|---|
| Lead | Contact, Lifecycle Stage = Lead | No conversion event; the Contact is permanent |
| Opportunity | Deal | Top-of-funnel through "wants to proceed", then a coarse outcome |
| Application / Offer / Product | **Not in HubSpot** | Lives in Pulse / our system |
| Account | Company | Largely unused (B2C) |

Structure:
- **Contact** = the person (permanent). One Contact → many Deals; a Deal → many Contacts
  (joint applicants), via HubSpot **associations**.
- **Deal** = one opportunity. In HubSpot the deal covers the **top-of-funnel** stages and
  then a **coarse outcome** mirrored from Pulse — not the granular application lifecycle.
- **Offer** = a human-readable attribute on the deal (e.g. "ADCB @ 4.19% 25yr"), plus
  optional drop-off signals for retargeting. **No Offer/Product objects, no catalogue sync.**
- **No custom objects in HubSpot** (Pro has none; and the reframe means we don't need them).
  The Application/Offer detail lives in Pulse.

HubSpot deal pipeline (top-of-funnel + outcome; confirm with sales):

`New → Contacted → Qualified → Agreement in Principle → Proceeding → [Handed to Pulse] → Won / Lost`

Everything after "Proceeding" is a coarse status reflecting Pulse — the detailed stages
(Documents → Underwriting → Valuation → Offer → Completed) live in **Pulse**.

---

## 4. Retargeting without products in HubSpot (Marcus's challenge)

Problem: a user reaches the offer-selection screen, picks nothing, and we want to retarget
them ("here are the offers you saw / your best options") — but we don't keep bank products
in HubSpot. Three workable patterns, in order of fit for Pro:

1. **Signal + snapshot on the record (recommended on Pro).** At drop-off, write lightweight
   properties to the contact/deal: e.g. `dropped_at = offer_selection`, and a human-readable
   `offers_seen_snapshot` ("ADCB 4.19%, ENBD 4.35%, …"). HubSpot segments on the signal;
   personalization tokens render the snapshot. Simple, no catalogue, works on Pro. Snapshot
   is point-in-time, not live.
2. **HubSpot triggers, our system composes (recommended for live offers).** HubSpot owns the
   segment/trigger ("reached offer selection, no offer in 24h") and notifies our system; our
   system pulls the current best offers from our catalogue and sends the push/email. Offers
   never enter HubSpot. Best fit since push isn't a native HubSpot channel anyway.
3. **HubDB bridge (later / higher tier).** Sync a small "current offers" table into HubDB via
   API and let HubSpot dynamic content read it. Note tiers: HubDB dynamic pages need Content
   Hub Pro/Enterprise; HubDB in programmable email needs Marketing Hub Enterprise.

Lucas's "pull from external systems for targeting" maps to patterns 2 and 3.

---

## 5. Integration approach

- **Auth:** HubSpot **Private App** access token (bearer, non-expiring, single-account).
  Not legacy API keys; OAuth only for multi-account apps.
- **PoC app:** we're building a small proof-of-concept app to validate this flow against a
  sandbox. It talks to HubSpot's API directly rather than relying on the unmaintained community
  library, and we'll grow it to cover the points in this plan. (Engineering detail lives with the code.)
- **Scopes (initial):** `crm.objects.contacts.read/write`, `crm.objects.deals.read/write`,
  `crm.schemas.contacts.read`, `crm.schemas.deals.read`, `crm.objects.owners.read`.
- **Scopes (later):** webhook subscriptions; HubDB scopes only if we adopt pattern 3.

---

## 6. Sync design

**Outbound (portal → HubSpot).** Lead row + outbox row in one DB transaction; the request
returns immediately. A background worker drains the outbox: resolve contact, resolve deal,
associate, persist returned ids, mark done; retry with backoff. (This is what the PoC app exercises today.)

**Inbound (HubSpot → portal) — lighter now.** Webhooks on contact/deal changes for
qualification + nurturing engagement. We no longer need to mirror granular application
state into HubSpot (that's Pulse), so inbound is mostly funnel/engagement signals.

**Reconciliation sweep.** Scheduled `hs_lastmodifieddate > lastRun` query catches anything
webhooks miss, plus a daily reconcile.

**Handoff — now HubSpot → Pulse.** When the user wants to proceed, the backend hands the
opportunity to Pulse (the system of record from here). HubSpot receives only a **coarse
outcome** back (in-processing / won / lost) for funnel reporting.

---

## 7. Data ownership & sync direction

| Data domain | Owner | Direction | Notes |
|---|---|---|---|
| Customer identity & inquiry | Customer portal | Portal → HubSpot | On create + customer-driven updates |
| Funnel/qualification (lifecycle, lead status, top-of-funnel deal stage, owner) | HubSpot | HubSpot → backend | Top-of-funnel only |
| Retargeting signals + offer snapshot | Backend → HubSpot | Portal/backend → HubSpot | Written for nurturing/retargeting |
| Application, documents, offers, products | **Pulse** | Pulse → backend → (coarse) HubSpot | HubSpot gets outcome only, not detail |
| Linking keys & stored ids | Backend DB | internal | The join across systems |

One owner per field; the owner's value wins.

---

## 8. Identity & de-duplication (all sources, incl. organic)

### Keys
- **`opportunity_id`** (was `portal_lead_id`) — **our** id for the opportunity, minted by the
  backend when an inquiry meaningfully begins, **regardless of source**. Unique on the Deal.
  This is the dedup anchor for every flow, partner or organic.
- **`partner_lead_ref`** (optional) — the Bayut/Dubizzle reference, stored as a plain attribute
  for traceability. Never the dedup key.
- **`lead_source`** — Bayut / Dubizzle / Organic-Web / Organic-App / Referral / …
- Contact match order: `portal_customer_id` → email → phone (reuse on hit; a 409 on email means
  the contact already exists → update it).

### Every inbound lead answers two independent questions
1. **Who is the person?** → resolve or create the Contact.
2. **Is this a new opportunity or a continuation?** → reuse or create the Deal.

The answers differ by how much we know about the person.

### Identity resolution (the Contact)
- **Authenticated** (logged into portal/app): we hold the user account → stored HubSpot contact
  id (and Pulse id). Deterministic — no matching needed.
- **Known, not logged in** (gave email/phone): match by email → phone; reuse or create.
- **Anonymous**: do **not** create a HubSpot contact yet (avoids junk/duplicate contacts). Hold
  the inquiry locally against an anonymous session + a provisional `opportunity_id`. Create the
  HubSpot contact only once we have an email/phone or they authenticate.

### Opportunity resolution (the Deal) — reuse vs create
Define the deal's state explicitly:
- **Open** = any top-of-funnel stage up to Proceeding, not Won/Lost/Abandoned.
- **Closed** = Won, Lost, or Abandoned (idle for N days — N is an open question, §11).

Rule:
- Contact has an **open** opportunity → **reuse it** (update the same deal).
- No open opportunity (none yet, or the last is Closed) → **create a new** opportunity
  (new `opportunity_id`, new deal). Same Contact, another Deal.

### Anonymous → known (stitching)
When an anonymous session becomes identified (login or email):
1. Resolve the Contact.
2. If that Contact already has an **open** opportunity, fold the anonymous session's captured
   data into it — do **not** create a second deal.
3. Otherwise, promote the anonymous `opportunity_id` into a real deal under that Contact.

(Whether to merge or always promote is an open question — §11.)

### Editing the right deal
Always **update by the stored HubSpot deal id**, persisted on our opportunity record (keyed by
`opportunity_id`, linked to the user account once known). When no stored id exists (e.g. after a
restart, or an organic lead with no customer id), fall back in order: search by `opportunity_id`,
then ask HubSpot for the contact's associated deals (v4 associations + a batch read of
`dealstage`) and reuse an **open** one. The rate-limited Search endpoint is never the hot path —
the associations GET + batch read avoid the search-index-lag duplicate risk. "Open" is defined by
`HubSpot:ClosedDealStages` config (see `hubspot-config-and-operations.md` §2).

### Worked examples
- **Sara (Bayut):** arrives with a partner ref → backend mints `opportunity_id`, stores
  `partner_lead_ref`, resolves/creates Contact, creates Deal, persists ids.
- **Omar (organic, authenticated):** visits the site directly, logged in, starts an inquiry
  while a previous one is **still open** → reuse the open deal; no new deal.
- **Omar, later:** his earlier deal was marked **Lost**; three months on he returns for a
  different property → **new** opportunity, new deal, same Contact.
- **Layla (organic, anonymous):** browses, drops at offer selection, never gives an email →
  held locally, **no** HubSpot contact yet. If she returns and logs in, stitch (above).

Custom properties: `Deal.opportunity_id` (unique), `Contact.portal_customer_id` (unique, if
available), `Contact.lead_source`, `Deal.partner_lead_ref`, plus retargeting signals.

---

## 9. Limits & reliability

- Rate limits: ~100–150 req/10s; Search ~4/s (keep off hot paths — use stored ids); batch 100/req.
- 429 → honour `Retry-After`; 5xx → exponential backoff; outbox/queue smooths bursts.
- Inbound: dedupe on event id, apply by `updatedAt`, reconcile on schedule.

---

## 10. HubSpot setup checklist (sandbox / dev test account)

- [ ] Private App created; token stored as a secret.
- [ ] `Deal.opportunity_id` — text, unique.
- [ ] `Contact.portal_customer_id` — text, unique (if a customer id exists).
- [ ] `Contact.lead_source`; `Deal.partner_lead_ref`; retargeting signal properties
      (`dropped_at`, `offers_seen_snapshot`).
- [ ] Top-of-funnel **Mortgage** pipeline + stages; record internal names.
- [ ] Build/test against a developer test account.

---

## 11. Decisions

### Decided
- **Plan: Pro** — no custom objects in HubSpot.
- **Shared backend** across customer portal and Pulse integration.
- **One Deal per opportunity**; new Deal only for a genuinely new opportunity.
- **HubSpot = top-of-funnel; Pulse = processing system of record** (2026-06-04 call). Current
  HubSpot ops build is throwaway.
- **No Offer/Product objects in HubSpot; no catalogue sync.** Offer = human-readable attribute.
- **Dedup anchor = our own `opportunity_id`**, source-agnostic; partner ref is just an attribute.

### Still open (resolve before building further)

*Living list — add new questions in the same format: **the question**, why it matters, and a
worked example. Running cast: Sara (Bayut), Omar (organic/authenticated), Layla (anonymous).*

1. **Retargeting mechanism.** Snapshot (pattern 1), HubSpot-triggers-we-compose (pattern 2),
   or HubDB (pattern 3)? Pattern 2 looks best on Pro. *Sara reaches offer selection, sees ADCB
   4.19% / ENBD 4.35% / FAB 4.40%, and selects none — how do we nudge her, and with which
   offers (the ones she saw, or the current best)? And if she selects ADCB but doesn't
   continue, do we retarget with that same offer or with alternatives?*
2. **Coarse status back from Pulse.** Which outcomes does HubSpot need, at what granularity —
   just in-processing / won / lost, or milestones like "offer issued"? *Sara proceeds, then
   Pulse rejects her at underwriting — does HubSpot flip her deal to Lost and drop her back
   into nurturing, and should the rep see the reason?*
3. **Contact-data ownership.** Do HubSpot edits to contact fields flow back to the portal?
   *A rep fixes Sara's mistyped phone in HubSpot — does the corrected number sync back to her
   portal record, or does the portal later overwrite it?*
4. **Organic "new opportunity" rule + abandoned threshold.** Exact condition for reusing an
   open deal vs creating a new one, and how long an idle open deal stays open before
   auto-closing. *Omar's organic deal has sat untouched for 30 days; he returns — reuse it or
   start fresh?*
5. **Anonymous → known stitching.** When an anonymous session becomes identified, merge it into
   an existing open deal or always promote to a new one? *Layla browsed anonymously and dropped
   at offer selection; she logs in a week later and already has an open deal from a prior visit
   — do we merge, or create a second?*
6. **Concurrency.** Do we ever allow more than one open deal per person (genuinely separate
   products at once), or strictly one open opportunity at a time? *Omar is exploring a primary-
   home mortgage and a buy-to-let simultaneously — one deal or two?*

---

## 12. Build order

> The PoC app now demonstrates phases 1–4 (outbound, outbox + worker, inbound webhooks with
> signature validation, reconciliation) plus the organic/anonymous identity logic — at PoC
> level (in-memory stores, sandbox). It also covers the "B-series" hardening: deal-stage name→id
> mapping, the inbound mirror actually updating local records for HubSpot-owned fields, and
> open-deal reuse via HubSpot associations. Webhooks use the new generic `crmObjects` format
> (the receiver parses both that and the legacy shape). Config knobs + webhook update steps are
> in `hubspot-config-and-operations.md`. Phases 5–6 and the open decisions remain.

- **Phase 0 — Setup & decisions.** Resolve §11; complete §10. *Done when: sandbox ready, decisions signed off.*
- **Phase 1 — Outbound MVP.** Contact + Deal, associate, dedup (partner + organic). *Done when: a lead → one contact + one deal.* (PoC app covers the basics; we grow it from here.)
- **Phase 2 — Outbox + worker.** Off the request path; retries, stored ids. *Done when: portal never blocks on HubSpot.*
- **Phase 3 — Inbound (light).** Webhooks for qualification/engagement; signature, ack, idempotent, echo-suppression. *Done when: HubSpot funnel changes land on our side.*
- **Phase 4 — Reconciliation.** Changed-since sweep + daily reconcile.
- **Phase 5 — Pulse handoff.** Hand the opportunity to Pulse at "proceed"; receive coarse outcome back. *Done when: end-to-end, with outcome visible in HubSpot.*
- **Phase 6 — Retargeting + hardening.** Chosen retargeting pattern; per-field ownership; monitoring.

---

## 13. Glossary (HubSpot terms)

- **Contact / Deal / Pipeline / Stage** — person / opportunity / ordered stages / one step.
- **Lifecycle Stage** — contact property, Lead → … → Customer.
- **Association** — typed link between records (contact ↔ deal).
- **Private App** — single-account API credential (bearer token).
- **Webhook subscription** — HubSpot push on object events (CDC analogue).
- **HubDB** — HubSpot's built-in, API-populated table for dynamic content (tier-gated for pages/email).
- **PRYPCO Pulse** — our processing system of record (post top-of-funnel).
- **Source of truth / owner** — the one system authoritative for a field.

---

## References
- HubSpot dev docs: Private Apps, CRM API v3/v4, Webhooks, Associations, HubDB.
- HubSpot KB: Lifecycle stages, personalization tokens, smart content.
- Diagrams: `hubspot-concepts-and-architecture.md`.
