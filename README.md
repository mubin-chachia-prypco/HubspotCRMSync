# HubSpot Lead Sync — Prototype (.NET 8)

> **Status: proof of concept.** This validates the HubSpot **top-of-funnel** integration
> for the B2C mortgage flow against a sandbox. It is intentionally light: state is
> in-memory, there's no database, and external calls target a sandbox. It's built to be
> productionised next (e.g. picked up in Claude Code). See the roadmap below and the
> design docs in [`docs/`](docs/) (`hubspot-integration-plan.md`, `hubspot-concepts-and-architecture.md`).

## What it does

An ASP.NET Core host that keeps the customer portal and HubSpot in sync, covering:

- **Outbound** — portal lead → HubSpot Contact + Deal, with de-duplication and association,
  driven through a transactional **outbox** + background **worker** (so the request path
  never blocks on HubSpot).
- **Identity & dedup** — resolves the person (customer id → email → phone) and the
  opportunity (reuse the open deal vs create a new one). Handles **organic** and
  **anonymous** leads: an unidentifiable anonymous lead is *held locally* rather than
  creating a junk contact.
- **Inbound** — a HubSpot **webhook** receiver with **v3 signature validation**,
  idempotency (per `eventId`), and **echo-suppression** (ignores changes our own
  integration made, to avoid sync loops).
- **Reconciliation** — a scheduled sweep that asks HubSpot "what changed since last time?"
  to catch anything webhooks don't emit.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Liveness |
| POST | `/leads` | Submit a lead (any source). Persists + enqueues, returns `202` with the opportunity id. |
| POST | `/webhooks/hubspot` | HubSpot webhook receiver. Validates the v3 signature, acks fast, processes async. |

## Quick start (once the sandbox is up)

Set three env vars and run — no code changes needed:

```bash
export HUBSPOT_TOKEN=pat-na1-xxxx           # private app access token (outbound calls)
export HUBSPOT_CLIENT_SECRET=xxxx           # app client secret (verifies inbound webhooks)
export HUBSPOT_BASE_URL=https://api.hubapi.com   # optional; this is the default
dotnet run                                  # listens on http://localhost:5080 by default
```

(For HubSpot, the API host is `https://api.hubapi.com` even for sandboxes — what makes it
hit the sandbox is the token, not the URL. Override the listen port with `ASPNETCORE_URLS`.)

**Prerequisite in the sandbox** (see plan §10): create custom properties
`Deal.opportunity_id` (unique), `Contact.portal_customer_id` (unique), `Contact.lead_source`,
`Deal.partner_lead_ref`, plus the retargeting signal props `Deal.dropped_at` /
`Deal.offers_seen_snapshot`, and the Mortgage pipeline + stages.

## API — payloads & responses

### `GET /health`
```
200 OK
{ "status": "ok" }
```

### `POST /leads`
Accepts a lead from any source. Persists + enqueues, returns immediately; the actual
HubSpot create/update happens asynchronously in the worker (outcome is logged). `source` is
one of `Bayut | Dubizzle | OrganicWeb | OrganicApp | Referral | Other`. Every field except
the eventual opportunity id is optional.

**1) Partner lead (Bayut), identified by email**
```bash
curl -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "Bayut",
  "partnerLeadRef": "BYT-99812",
  "email": "sara@example.com",
  "firstName": "Sara",
  "lastName": "K",
  "phone": "+971501234567",
  "dealName": "Dubai Marina 2BR",
  "pipelineStage": "qualified",
  "amount": 1850000
}'
```
```
202 Accepted
{ "opportunityId": "OPP-8573dff3542048af9dacdc7fc144dcde", "queued": true }
```
→ worker resolves/creates the Contact, creates the Deal (tagged with the minted
`opportunity_id` + `partner_lead_ref`), and associates them.

**2) Organic, authenticated customer**
```bash
curl -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "OrganicWeb",
  "isAuthenticated": true,
  "customerId": "CUST-1024",
  "email": "omar@example.com",
  "firstName": "Omar",
  "dealName": "JVC Townhouse",
  "pipelineStage": "contacted"
}'
```
```
202 Accepted
{ "opportunityId": "OPP-<generated>", "queued": true }
```
→ matched on `portal_customer_id`/email; reuses the open deal for that customer if one exists.

**3) Anonymous drop-off (retargeting signals) — held locally**
```bash
curl -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "OrganicWeb",
  "anonymousSessionId": "sess-abc123",
  "droppedAt": "offer_selection",
  "offersSeenSnapshot": "ADCB 4.19%, ENBD 4.35%, FAB 4.40%"
}'
```
```
202 Accepted
{ "opportunityId": "OPP-<generated>", "queued": true }
```
→ no email/phone/customer id, so **no HubSpot contact is created**; the lead is held locally
(worker logs `held=True`) until the person identifies themselves.

**4) Continuation / update by known ids (subsequent step)**
```bash
curl -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "opportunityId": "OPP-8573dff3542048af9dacdc7fc144dcde",
  "source": "Bayut",
  "hubSpotContactId": "123456789",
  "hubSpotDealId": "987654321",
  "phone": "+971500000000",
  "pipelineStage": "agreement_in_principle"
}'
```
```
202 Accepted
{ "opportunityId": "OPP-8573dff3542048af9dacdc7fc144dcde", "queued": true }
```
→ updates the exact records by stored id (no search), so it always edits the right deal.

### `POST /webhooks/hubspot`
HubSpot posts an array of events plus the `X-HubSpot-Signature-v3` and
`X-HubSpot-Request-Timestamp` headers. The receiver validates the v3 signature, acks fast,
and processes asynchronously (idempotent + echo-suppressed).

Sample body HubSpot sends:
```json
[
  {
    "eventId": 1234567890,
    "subscriptionType": "contact.propertyChange",
    "objectId": 123456789,
    "propertyName": "lifecyclestage",
    "propertyValue": "salesqualifiedlead",
    "occurredAt": 1733400000000,
    "changeSource": "CRM_UI",
    "sourceId": "userId:5551234"
  }
]
```
```
200 OK     # valid signature, fresh timestamp
401        # missing/invalid signature, or timestamp older than 5 minutes
```

**Test the webhook locally** (generates a valid signature with the same formula HubSpot uses):
```bash
SECRET=testsecret                       # must equal HUBSPOT_CLIENT_SECRET
URI='http://localhost:5080/webhooks/hubspot'
TS=$(date +%s)000
BODY='[{"eventId":1,"subscriptionType":"contact.propertyChange","objectId":123,"occurredAt":'$TS',"changeSource":"CRM_UI"}]'
SIG=$(printf '%s' "POST${URI}${BODY}${TS}" | openssl dgst -sha256 -hmac "$SECRET" -binary | base64)
curl -X POST "$URI" -H 'Content-Type: application/json' \
  -H "X-HubSpot-Request-Timestamp: $TS" -H "X-HubSpot-Signature-v3: $SIG" -d "$BODY"
# -> 200; an event with changeSource "INTEGRATION" (our own) would be accepted then skipped as an echo
```

### What lands in HubSpot
For reference, a `/leads` call maps to these CRM property payloads:
- **Contact:** `portal_customer_id`, `email`, `phone`, `firstname`, `lastname`, `lead_source`.
- **Deal:** `opportunity_id`, `dealname`, `dealstage`, `partner_lead_ref`, `lead_source`,
  `dropped_at`, `offers_seen_snapshot`, `amount` — then a default association to the contact.


## Layout

```
Program.cs                       host, DI, endpoints
Models.cs                        DTOs, enums, local opportunity record
HubSpot/HubSpotClient.cs         REST client (search/create/update/associate/changed-since) + retry
HubSpot/WebhookSignatureValidator.cs   v3 signature validation
Sync/Stores.cs                   in-memory stores (opportunity/DB seam, outbox, idempotency, echo guard)
Sync/LeadSyncService.cs          identity resolution + dedup (the core logic)
Sync/OutboxWorker.cs             drains the outbox -> HubSpot
Sync/InboundEventProcessor.cs    webhook handling (idempotent, echo-suppressed, ownership-aware)
Sync/ReconciliationWorker.cs     scheduled changed-since sweep
```

## Roadmap (mirrors the plan's phases)

- [x] Outbound: Contact + Deal create/update, association, de-duplication
- [x] Organic + anonymous identity resolution (anonymous held locally)
- [x] Outbox + background worker (retry/back-off)
- [x] Inbound webhooks + v3 signature validation (idempotency + echo-suppression)
- [x] Reconciliation sweep (scheduled changed-since)
- [ ] **Pulse handoff** + coarse status back (model is ready; integration not built)
- [ ] Anonymous → known **stitching** rule (decision open — see plan §11)
- [ ] Find-open-deal-for-contact via HubSpot **associations** (currently uses the local store)

## Seams to replace when productionising

- **In-memory stores → a real DB.** `IOpportunityStore`, `IOutbox`, `IProcessedEvents`,
  `IEchoGuard` are interfaces with in-memory impls; swap for DB-backed ones. The outbox row
  and the lead row should be written in one transaction.
- **Inbound processing & reconciliation currently log** the change; wire them to update the
  local mirror for HubSpot-owned fields.
- **Pulse handoff** isn't implemented — `OpportunityState` and the ownership notes mark where it goes.
- **Retargeting** (offers) is captured as snapshot properties on the deal; live offers are a
  send-time concern for our own system (see plan §4), not modelled here.

> Open design questions (contact-data ownership, handoff trigger, application surface,
> abandoned-deal threshold, stitching, concurrency) are tracked in the plan doc, §11.
