# Middleware Spec — .NET Producer + Azure Function Adapter (JS)

**Status:** Building · **Updated:** 2026-06-17
**Supersedes:** the "external .NET service does all HubSpot work" model and the earlier
"in-HubSpot serverless adapter" idea (blocked — see §2).

> **FINAL SHAPE (2026-06-17) — read this first.** The adapter is an **Azure Function (Node.js)**,
> **not** a Cloudflare Worker. The company runs on Azure (instamortgage uses Blob/Service Bus +
> Key Vault via a managed identity), so the adapter lives on Azure — no new vendor for a ~1-year
> tool. Auth from the .NET side is the **managed identity → Entra "Easy Auth"** bearer (no shared
> secret). The .NET side mirrors instamortgage infra (EF Core/Npgsql + `Prypto.ServiceBusHelpers`)
> and will be absorbed into instamortgage. Where this doc says "Cloudflare Worker" below, read
> "Azure Function." Implementation: `HubspotApps/hubspot-adapter-function/` (+ its `SETUP.md`).

Single source of truth for the new two-piece middleware. Read §1–§4 first.

---

## 1. Goal & principle

**Zero HubSpot-specific code in our own product codebase.** Swap CRM later → throw away one
isolated piece (the adapter); everything else keeps working. We split the middleware into two
parts, each with exactly one job:

| Piece | Repo | Job | HubSpot-aware? |
|---|---|---|---|
| **Forwarder** | `HubspotCRMSync` (.NET) | Receive UI posts/patches, queue, deliver **at-least-once** (retry + dead-letter) | **No** — just a URL, a payload, a success code |
| **Adapter** | `HubspotApps` (Cloudflare Worker, JS) | Map the generic payload → HubSpot objects; find-or-create; update; associate; timeline notes | **Yes — the only HubSpot-aware code** |

---

## 2. Tier reality & the pivot (decision log)

Portal account tiers (confirmed from prod **Products & Add-ons**, 2026-06-17): **Marketing Hub
Professional, Sales Hub Enterprise (14 seats), Service Hub Professional, Data Hub Enterprise**,
+2 Enterprise core seats. **No Content Hub at all** (not even Pro). This is the real production
entitlement, not the inflated sandbox.

Consequences:
- ✅ **Custom objects** (Application/Offer/Property) are fine — Sales Hub Enterprise covers them.
- ❌ **Public serverless *endpoint* functions need Content Hub Enterprise** → Juan's in-HubSpot
  `/ingest` POC **cannot go to production**. Dropped.
- ✅ **Decision (2026-06-17):** the adapter is a **Cloudflare Worker** (Juan's suggestion). Same
  architecture, but the HubSpot-specific "box" runs on infra we control — no tier gate, real
  local testing (`wrangler dev`), reuses Juan's `@hubspot/api-client` JS, and swaps cleanly if we
  change CRM. Lower risk than the in-HubSpot path, not higher.

Other decisions:
- **Adapter lives in the `HubspotApps` repo** (the natural HubSpot-specific home), even though it
  deploys to Cloudflare, not HubSpot.
- **Idempotency = find-or-create** (no separate key store) + 409-as-success. Simple for PoC.
- **Field mapping lives in the adapter.** It is HubSpot-specific by definition.

---

## 3. Architecture

```
  ┌──────────┐  POST/PATCH      ┌─────────────────────────────┐  POST /ingest     ┌──────────────────────────────┐  CRM v3/v4  ┌─────────┐
  │ Portal / │  generic JSON    │ FORWARDER (HubspotCRMSync)  │  generic JSON +   │ ADAPTER (HubspotApps)        │ ──────────► │ HubSpot │
  │ Pulse UI │ ───────────────► │                             │  shared secret    │ Cloudflare Worker (JS)       │             │   CRM   │
  └──────────┘                  │ • durable outbox (queue)    │ ────────────────► │ • envelope → CRM mapping     │             └─────────┘
                                │ • background worker         │                   │ • find-or-create by extId    │
                                │ • retry/back-off → DLQ      │ ◄──────────────── │ • diff → timeline Note       │
                                │ • waits for 2xx             │  {ok, ids, action}│ • associations               │
                                │ • NO HubSpot code           │                   │ • idempotent (find-or-create)│
                                └─────────────────────────────┘                   └──────────────────────────────┘
                                       CRM-AGNOSTIC                          CRM-SPECIFIC · @hubspot/api-client · token in CF secrets
```

**Why keep the .NET forwarder** (vs. UI → Worker directly): guaranteed delivery needs a durable
queue, retry, and dead-letter — the forwarder's whole reason to exist (Arjhun's point: worker
waits for 2xx, retries N times, else DLQ). The outbox also sits naturally next to the backend's
DB (lead row + outbox row in one transaction). A Worker *could* use Cloudflare Queues, but we keep
queueing on our side.

---

## 4. The contract (the only thing both sides agree on)

A single generic **envelope**. The forwarder treats it as opaque *except* `idempotencyKey`. The
adapter interprets all of it. **FE has full freedom to design payload shapes** (per §8), as long as
they fit this envelope.

```jsonc
{
  "idempotencyKey": "uuid-v4",            // dedupe retries; REQUIRED
  "objectType": "lead | contact | deal | application | property | offer",
  "operation":  "upsert | update | create",
  "externalId": "portal-side stable id",  // adapter finds-or-creates by this (→ a unique CRM prop)
  "properties": { /* portal field names → values (per-screen, §8) */ },
  "associations": [                        // optional
    { "objectType": "deal", "externalId": "OPP-123", "label": "applicant" }
  ],
  "occurredAt": "2026-06-17T10:00:00Z"     // note timestamp + ordering
}
```

- `properties` use **portal field names**; the adapter renames to HubSpot props. The forwarder
  never looks inside.
- `externalId` resolves the record without a search-index race (write it to a unique HubSpot
  property per object — e.g. `portal_lead_id`, `opportunity_id`, `portal_application_id`).
- `upsert` is the default for the portal flow.

---

## 5. Forwarder spec (`HubspotCRMSync`, .NET)

The repo already has the right skeleton (`InMemoryOutbox`, `OutboxWorker`, retry/back-off in
`HubSpotClient.SendAsync`). It gets **simpler**.

**Changes**
- **Ingress:** generalise `POST /leads` to a `POST /ingest` that accepts the §4 envelope for any
  `objectType`; persist to outbox; return `202`.
- **Worker:** replace the `HubSpotClient`/`LeadSyncService` calls with a single
  `POST {AdapterIngestUrl}` (envelope body + shared-secret header). 2xx = success; non-2xx/timeout
  → retry with existing back-off; after `MaxAttempts` → **dead-letter**.
- **Delete HubSpot-specific code:** `HubSpot/HubSpotClient.cs`, the mapping/dedup in
  `LeadSyncService.cs`, deal-stage/association/property logic. (History keeps them; the adapter
  replaces them.)
- **Config:** `Forwarder: { AdapterIngestUrl, IngestSharedSecret }`. No HubSpot token in the
  forwarder anymore.

**Keeps:** transactional outbox + worker + retry/back-off; in-memory store seams with
"swap-for-DB in prod" comments.

**New seams:** `IDeadLetterQueue` (in-memory PoC); `IAdapterClient` (thin typed wrapper over the
one outbound POST, retry/JSON like the old `HubSpotClient.SendAsync`, CRM-agnostic).

---

## 6. Adapter spec (`HubspotApps`, Cloudflare Worker, JS)

### Location & runtime
New Worker project in `HubspotApps/` (e.g. `adapter-worker/`), deployed with **Wrangler**. Reuses
Juan's function logic (`@hubspot/api-client`, 409-as-success). The HubSpot **Private App token**
is a **Cloudflare secret** (`HUBSPOT_TOKEN`); the shared secret is `INGEST_SHARED_SECRET`.

> The existing `TestCRMSync` HubSpot project (SyncApp) stays as the **token/scope issuer** — it's
> the Private App that grants scopes and issues the token the Worker uses. We still add
> `crm.objects.notes.write` there and reinstall (per `HUBSPOT_PROJECTS.md`).

### Layout
```
HubspotApps/adapter-worker/
  wrangler.toml             # routes, vars, secrets binding
  package.json              # @hubspot/api-client
  src/
    index.js                # fetch handler: auth → route → respond {statusCode, body}
    envelope.js             # parse + validate the §4 envelope
    mapping.js              # portal field names → HubSpot prop names, per objectType (§7/§8) — TODO from live schema
    resolve.js              # find-or-create by externalId (unique prop), per object / custom-object type id
    notes.js                # diff before/after → POST /crm/v3/objects/notes (the activity-logging task)
    associations.js         # default association type ids (note→deal 214, note→contact 202; verify via /crm/v4 labels)
```

### Responsibilities
1. **Auth the caller** — reject unless the request carries `INGEST_SHARED_SECRET`. The Worker URL
   is public; this is the gate. `401` otherwise.
2. **Validate** the envelope; `400` on bad shape.
3. **Resolve** the target record (§16): a **Contact** on first touch by the identity chain
   `partner_lead_ref` → `email` → `phone`; otherwise (and on every later page) by the object's stable
   `externalId` prop → update; else create. 409 = idempotent success. Leads/Deals are not
   person-deduped (multiple per Contact; co-applicant — §16).
4. **Map** portal `properties` → HubSpot property names per `objectType` (§7). Custom objects
   (Application/Offer/Property) use their **object type id**, not a name.
5. **Diff & note** (carry-over of the original task): on update, fetch current values, diff vs.
   incoming, `POST /crm/v3/objects/notes` (`hs_timestamp` + `hs_note_body`:
   "Updated from portal: • Amount: 1,850,000 → 1,790,000"), associated to the record. Skip if
   nothing changed.
6. **Associate** per the envelope (application→deal, contact→deal with applicant/co-applicant
   label, etc.).
7. **Idempotency = find-or-create** (no key store). Notes: a duplicate timeline note is the one
   retry risk; acceptable for PoC — call out in tests. (Hardening later if needed.)
8. **Return** `{ statusCode, body: { ok, action, objectType, id } }`. Non-2xx → forwarder retries.

### Scopes (in SyncApp `app-hsmeta.json` → `requiredScopes`, then upload + **reinstall**)
Present: contacts r/w, deals r/w, custom objects r/w (+sensitive), schemas custom r/w.
**Add:** `crm.objects.notes.write`. Reinstall via Distribution tab → new token (scope change needs
reinstall, not just rotation — `HUBSPOT_PROJECTS.md`).

---

## 7. Data model

> **Source of truth = the live HubSpot sandbox** (decided 2026-06-17). Miro is the *design reference*;
> where the two differ, the sandbox wins and the gap is logged in §7a. `mapping.js` is filled from the
> sandbox, never from Miro field names.

| Portal `objectType` | HubSpot type | Resolve key (unique prop) | Notes |
|---|---|---|---|
| `contact` | Contact | `email` / `portal_customer_id` | applicant; co-applicant & bank-contact via association labels |
| `lead` | **Lead** (object) | `portal_lead_id` | top-of-funnel; own pipeline |
| `deal` | Deal | `opportunity_id` | the mortgage opportunity; mirrors main Application |
| `application` | **Custom object** | `portal_application_id` | up to 3 per Deal (one per bank); APRO or Manual |
| `offer` | **Custom object** | `portal_offer_id` | selected offer links Deal ↔ Application ↔ Bank |
| `property` | **Custom object** | `portal_property_id` | subject property |
| (`bank`) | Company | — | reference data, not portal-written |

Pipelines (for stage maps in `mapping.js`):
- **Lead:** New → In Progress → Won (→ converts to Deal) / Not Eligible (→ nurture, *never* Lost) / Lost.
- **Deal:** New → Offer Selection → Docs Collection → Credit Review → Bank Submission → Pre-Approved → Valuation → FOL → Disbursal → Property Transfer → Closed Won / Closed Lost.
- **Application:** New → Document Collection → Credit Review → Form Filling → Bank Submission → Pre-Approved (Won) / Rejected (Lost); **APRO fast-path** skips manual stages.

> **Custom-object type ids captured 2026-06-17** (application `2-203889439`, property `2-203890683`)
> from the live sandbox (`GET /crm/v3/schemas`, portal 148631333). Field maps written for
> contact/deal/application/property; lead/offer still blocked. See §7a for where live drifts from this
> Miro model.

---

## 7a. Live sandbox vs designed model (drift log)

Snapshot 2026-06-17. **Live** = CRM API (authoritative) except **Lead** (unread — Private App lacks
`leads-read`) and **Bank/Company** (not pulled). **Miro** = board data-model doc + Object Map ERD.

| Object | Miro (designed) | Live (sandbox) | Drift |
|---|---|---|---|
| Contact | full_name, date_of_birth, nationality, email, phone; **email + phone unique** | + custom `employment_type`, `residency_status`, `portal_customer_id`, `lead_source`, `years_in_business`; **only email unique** | `residency_status` not `nationality`; **phone NOT unique**; no `contact_type`/role for bank contacts |
| Lead | object w/ `monthly_salary`, `other_income`, `monthly_debts`, `credit_card_limit`, eligibility | standard Leads object + pipeline; **fields unreadable**; financials appear on **Deal** | Can't confirm Lead fields; affordability likely lives on Deal, not Lead |
| Deal | stage, applicant_type, borrowing_amount | **32 custom props**: full `has_*`/`num_*` liabilities, `monthly_salary`, `borrowing_amount`, `loan_term`, `total_credit_limit`, `monthly_non_banking_borrowing`, `apro_verification_status`, `partner_lead_ref`, snapshots | Live Deal absorbed Miro's *Lead* financials **+** a liabilities block Miro doesn't show |
| Application (`2-203889439`) | stage, substage, path | `application_name`, `application_path`[apro,manual] **+ dup** `path`[APRO,Manual], `bank`, `substatus` | **duplicate path field**; `substage`→`substatus` |
| Property (`2-203890683`) | community, type, price, status | `property_name`, `community`, `price`, `property_type`, `status` | matches; live adds `property_name` |
| Offer | custom object: rate, term, type | **does not exist** | whole object missing |
| Bank | Company w/ `apro_enabled` | not pulled | unverified |
| Bank Contact | Contact w/ `contact_type=Bank`, role | no such props on Contact | missing live |
| Document | native (MVP) | no custom object | consistent |

**Deal pipeline:** Miro 12 stages → live "Mortgage" (`3878730980`) **9 stages**, collapsing Offer
Selection / Docs Collection / Credit Review / Bank Submission into one **Qualifying** stage.
Application Manual/APRO pipelines match.

**Field-name drift (financials):** `monthly_debts`→`monthly_non_banking_borrowing`/`existing_liabilities_total`;
`credit_card_limit`→`total_credit_limit`; `other_income`→ no direct prop (split across
`annual_bonus`/`annual_commission`/`annual_rent_receivables`).

**Lead is a standard object, not a custom one.** It already exists in the sandbox with the Miro
pipeline (New → In Progress → Won / Not Eligible / Lost), so there is **nothing to create** — the only
action is granting the Private App `leads-read`/`leads-write` + reinstall to read/write it. `mapping.js`
already targets it (`lead → 'leads'`). The **Lead → Deal conversion is a HubSpot workflow** (HubSpot
team owns workflows), not the adapter's job — we upsert the Lead, then later the Deal.

---

## 8. Customer journey → object → payload (Figma: "Full Scope – Flow Diagram V4")

The portal screens drive which object each payload targets. FE shapes the per-screen `properties`;
this is the screen→object routing:

| Screen(s) | Captured data | `objectType` / `operation` |
|---|---|---|
| Dubizzle / partner entry | source, partner ref | `lead` / upsert |
| **Affordability Calculator (Salaried/…)** | income, obligations, eligibility inputs | `lead` / upsert |
| Verification Code (OTP) | phone/email verified → identity | `contact` / upsert |
| Consent → Front/Back ID → Selfie → Processing/Verifying | UAE PASS / KYC (APRO) | `contact` update + `application` create (APRO path) |
| **Mortgage Proposal** | eligibility result, offers shown/selected | `deal` upsert + `offer` create; on eligibility+account → Lead converts to `deal` |
| Real Estate / Additional Questions | extra qualification | `lead`/`deal` update |
| **Customer Portal** (application, docs, property) + Submitting… | account, application progress, property, docs | `deal` + `application` + `property` upserts |
| Read Key Facts Statement / KFS checked | consent/disclosure flags | `deal`/`application` update |

> Exact form fields per screen can be read on demand from Figma (`get_design_context` per frame)
> when we finalise each payload with FE. Object-level routing above is enough to build the
> envelope + mapping skeleton.

---

## 9. Reliability & idempotency (end-to-end)

- **At-least-once** delivery from the forwarder (retry → DLQ) ⇒ the adapter **must be idempotent**:
  guaranteed by (a) find-or-create on `externalId`, (b) 409-as-success.
- **Duplicate-note risk:** a retried update could post a second timeline note. Accepted for PoC;
  note it in tests. Hardening option later: short dedupe on `idempotencyKey`.
- **Ordering:** process one envelope per record where it matters; use `occurredAt` if HubSpot shows
  out-of-order updates. PoC: best-effort.

---

## 10. Security

- Worker `/ingest` is a **public URL**; the **shared secret** header (`INGEST_SHARED_SECRET`) is
  the gate — forwarder holds it in config/secret store, Worker in CF secrets. Rotate together.
- HubSpot token (`HUBSPOT_TOKEN`) lives only in **Cloudflare secrets**; never in the forwarder.
- Optional hardening later: HMAC-sign the body (like the old webhook v3 scheme).

---

## 11. Repos & layout

- **`HubspotCRMSync`** (.NET) — the forwarder. Strips HubSpot code; gains `/ingest`, `IAdapterClient`,
  `IDeadLetterQueue`. Docs updated here.
- **`HubspotApps`** — the HubSpot-specific home:
  - `TestCRMSync/` — the SyncApp HubSpot project (token/scope issuer; add notes scope + reinstall).
  - `adapter-worker/` — **new** Cloudflare Worker (the adapter); Wrangler deploy.

---

## 12. Open items

1. **Cloudflare account/secrets** — who provisions the CF account, `HUBSPOT_TOKEN`,
   `INGEST_SHARED_SECRET`, and the Worker route/domain. (Mubin wires CF; I scaffold code.)
2. **Custom-object type ids + exact property names** — from live schema (HubSpotDev MCP in a fresh
   session, or `GET /crm/v3/schemas`). Fills `mapping.js`.
3. **Per-screen field lists** — finalise with FE; readable from Figma per frame when needed.
4. **Association label vocabulary** — applicant/co-applicant/bank-contact, application→deal,
   offer↔application↔bank.

---

## 13. Implementation plan (phased)

- **Phase 0 — Sign-off.** This plan. ✅ decisions: CF Worker adapter in HubspotApps; find-or-create.
- **Phase 1 — Adapter skeleton (Worker).** Scaffold `adapter-worker/` (wrangler, package, index);
  shared-secret gate; envelope parse/validate; `contact` upsert by `externalId` (port Juan's fn);
  `wrangler dev` locally. Add `crm.objects.notes.write` to SyncApp + reinstall.
- **Phase 2 — Forwarder rewrite.** `/ingest` envelope ingress; worker POSTs to the Worker;
  `IDeadLetterQueue`; strip HubSpot code; config; update README/docs.
- **Phase 2.5 — Dubizzle lead intake (§15).** `inbound_leads` table (UUIDv7 PK-as-token, jsonb
  payload); `POST /intake/dubizzle` (store-only, returns token) + `GET /intake/dubizzle/{token}`
  (one-time, 60s TTL → prefill). No HubSpot write on intake.
- **Phase 3 — Full mapping.** `mapping.js` + `resolve.js` for lead/deal/application/offer/property
  from live schema; associations; stage maps (§7/§8). *(Partial: contact/deal mapped; application &
  property type ids filled 2026-06-17; lead/offer blocked — see §7.)*
- **Phase 4 — Activity logging.** `notes.js` diff→note (the original task).
- **Phase 5 — Hardening.** DLQ replay, logging/observability, tests (incl. duplicate-note check),
  optional HMAC + idempotency dedupe.

---

## 14. What changes vs. the old docs

`hubspot-integration-plan.md`, `README.md`, `hubspot-app-and-auth-setup.md` describe the external
.NET service doing all HubSpot work (plus already-removed webhook/mirror code, `5f1d72c`). They'll
be updated to point at this forwarder + Worker-adapter split once Phase 1–2 land.

---

## 15. Dubizzle / Bayut lead intake (top-of-funnel entry → token → prefill)

**Status:** designed, not built · **Decided:** 2026-06-17 (from the Dubizzle email thread w/ Jonathon Padron)

Dubizzle is the primary top-of-funnel entry. When a user submits the Dubizzle lead form, **their
Lambda posts the lead twice**:
1. To **their own CRM** — which syncs to our HubSpot via the existing CRM integration (this is where
   their dedup / sync-error retention lives). *Not our concern here.*
2. **Directly to us** — we store it and return a **one-time token**. Dubizzle redirects the user to a
   PRYPCO URL with that token in the query string; our FE redeems it to **prefill the affordability
   calculator**.

```
  Dubizzle form ─► Dubizzle Lambda ─┬─► Dubizzle CRM ─► (existing) ─► our HubSpot
                                    │
                                    └─► POST /intake/dubizzle ─► inbound_leads (store only)
                                            ◄── { token = UUIDv7 } ──┘
  User ─► redirect to prypco.com/...?token=<uuidv7>
  FE   ─► GET /intake/dubizzle/{token} ─► payload ─► prefill calculator (token burned, 60s TTL)
```

### Decisions (locked)
- **UUIDv7 as the primary key AND the URL token.** Time-ordered ⇒ sortable / index-friendly; ~74
  random bits ⇒ not enumerable (avoids the IDOR risk a sequential id would create in a public URL).
  `.NET 9 Guid.CreateVersion7()`, stored as Postgres `uuid`. **We mint it server-side on insert —
  Dubizzle never sends an id; the token in our response IS this id**, which Dubizzle echoes into the
  redirect URL and FE hands back on redeem.
- **We do not care about Dubizzle's payload schema.** The lead is stored as an **opaque `jsonb` blob**
  and returned verbatim on redeem — the .NET side never validates or maps their field names (keeps the
  producer "dumb"). FE is the only thing that reads inside the blob, to prefill the calculator.
- **Store-only on intake.** No HubSpot write. Dubizzle sends a copy for *every* lead regardless of
  click-through; auto-pushing all of them to HubSpot would pollute the funnel. The HubSpot Lead/Deal
  is created later via the normal envelope → adapter path **when the user actually engages** with the
  calculator. (The HubSpot copy for non-engaging leads still arrives via path #1, Dubizzle's CRM.)
- **One-time + ~60s TTL.** Redeem returns the payload iff `expires_at > now() AND consumed_at IS NULL`,
  then stamps `consumed_at`; otherwise `410 Gone`. Matches Jonathon's "on failure, error out and let
  the user fill the next page manually" — no elaborate resend. A Dubizzle resend just inserts a new
  row with a fresh token; dedup is Dubizzle's CRM responsibility, not ours.

### Table (`inbound_leads`) — new EF entity + config alongside the outbox
| column | type | notes |
|---|---|---|
| `id` | `uuid` (v7) | PK; also the URL token |
| `source` | `text` | `'dubizzle'` (room for `'bayut'`, etc.) |
| `payload` | `jsonb` | raw lead as received; schema-flexible |
| `created_at` | `timestamptz` | |
| `expires_at` | `timestamptz` | `created_at + ~60s` |
| `consumed_at` | `timestamptz?` | set on first successful redeem (one-time) |

### Endpoints (on the .NET producer, mirroring the `/ingest` minimal-API style)
- `POST /intake/dubizzle` — Dubizzle Lambda → us. Insert row, return `{ token }`. **Auth: a static
  Bearer token** Dubizzle sends in the `Authorization: Bearer …` header, held in our secrets (Key
  Vault in prod). Validated only when configured (`Intake:Dubizzle:BearerToken`), so local dev needs
  none. **Decided 2026-06-17 — bearer token, not HMAC.**
- `GET /intake/dubizzle/{token}` — FE → us. Returns the stored payload (or `410`); burns the token.
  Gated by the token itself (FE is unauthenticated at landing); consider returning only the
  prefill-relevant fields.

### Link to external id (see §16)
The intake token is **per-visit and ephemeral** — a returning customer gets a *new* `inbound_leads.id`
each time, so it is NOT the durable identity. When the user engages, the first real upsert resolves the
Contact via the identity chain (§16) and carries our own stable portal id; Dubizzle's lead reference is
stored as `partner_lead_ref` (identity key + attribution, never the token).

### Open items
1. **Inbound Bearer token value + rotation** — agree the token string with Dubizzle and store it in
   our Key Vault (`Intake:Dubizzle:BearerToken`). *(The only thing we need from them — not their payload shape.)*

> Dubizzle's payload schema and which fields prefill the calculator are **FE concerns, not ours** —
> our side stores/returns the blob opaquely. No action needed here.

---

## 16. External id & idempotency (how upsert stays "update, not duplicate")

**Decided:** 2026-06-17.

Two concerns, kept separate: **(a) identity dedup** — is this the same person/record we already have?
and **(b) a stable per-record handle** — so every later page hits that exact record, idempotently.

**Contact identity — precedence chain (cold start).** On the **first** Contact upsert the Function
resolves by `partner_lead_ref` (Bayut ref) → `email` → `phone`; first hit wins (found → update +
backfill the missing identifiers; none → create). **Universal, not Bayut-specific** — organic leads
(no ref) fall through to email → phone, so the same logic also covers leads that never touched the
Dubizzle flow. One person ⇒ one Contact. Lives in `resolve.js` (it must query the CRM to search each
key); the .NET producer just passes whatever identifiers it has in the envelope.

**After first input — resolve by the stable id (warm path).** Once an object exists we carry its
stable id, and **every subsequent page resolves that record directly by id** — exact match, no
precedence search, no create race. The carried id is **our own** portal id (`portal_customer_id`,
`portal_lead_id`, `opportunity_id`, `portal_application_id`, `portal_property_id`), minted by us and
written to a unique HubSpot prop — **NOT** HubSpot's internal record id, so FE and the producer stay
CRM-agnostic. (The Function also returns HubSpot's record id in its `{ok, action, id}` response —
fine as an internal fast-path, but not what FE carries.) Net: **precedence chain = cold-start dedup;
stable id = the handle for everything after.**

**Leads/Deals — multiple per Contact (NOT person-deduped).** Co-applicant support is coming: a person
may be applicant on one and co-applicant on another, and Contact↔Deal is many-to-many with
applicant/co-applicant labels (§7). So Leads/Deals are never collapsed by person identity; each keeps
its own minted id so a repeat send updates *that* record without merging distinct leads. Bayut's ref
is stored as `partner_lead_ref` (identity key #1 **and** attribution). The §15 intake token stays
ephemeral — never an identity key.

**Affordability placement (open).** Pre-conversion these edits target the **Lead** (calculator is a
Lead-stage activity, §8); post-conversion the **Deal**. ⚠️ In the live schema the financials sit on
**Deal** while Miro models them on Lead — settle the Lead-vs-Deal placement with FE + the HubSpot team.

**Uniqueness (blocker).** Idempotent *create* is only race-free if the resolve prop is UNIQUE in
HubSpot. Current: `portal_customer_id` / `opportunity_id` exist but **non-unique**;
`portal_application_id` / `portal_property_id` **don't exist**; `portal_lead_id` unverified (no
`leads-read`). `resolve.js` does search-then-create meanwhile (race window on first create only —
the warm path above already avoids it for everything after). Fix = add the missing `portal_*_id`
props + set `hasUniqueValue=true` (then create-conflict 409 = idempotent success).
