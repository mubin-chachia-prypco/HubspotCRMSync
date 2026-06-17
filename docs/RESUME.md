# Resume / Handoff Note

Restart-friendly note so a fresh Claude Code session (or teammate) can pick this up. **Read this
first, then `docs/forwarder-adapter-spec.md`.**

_Last updated: 2026-06-17 (after the Azure pivot + Phase 1/2 build)_

> ⚠️ **If you saw an older prompt mentioning a "Cloudflare Worker", `adapter-worker/`, a
> "shared-secret gate", or "nothing is built yet, start at Phase 1" — that is OUTDATED.** The
> adapter is an **Azure Function**, auth is **Managed Identity → Entra**, and **Phase 1 + 2 are
> already built and committed.** This note is the current truth.

---

## Architecture (current, final)

```
instamortgage / portal (C#)  ──Azure Service Bus──►  HubSpotConsumer (C#)  ──HTTPS + Managed Identity bearer──►  Azure Function adapter (Node/JS)  ──►  HubSpot
   HubspotCRMSync (this repo) — CRM-AGNOSTIC producer                                       HubspotApps/hubspot-adapter-function — ALL HubSpot logic
```

- **.NET side = HubSpot-free producer.** Mirrors InstaMortgageService infra (EF Core 9 + Npgsql
  Postgres + outbox; `Prypto.ServiceBusHelpers`). Will be absorbed into instamortgage.
- **Adapter = Azure Function (Node, `@hubspot/api-client`).** The only HubSpot-aware code.
- **Auth:** instamortgage's **managed identity** → token for the Function's Entra app (Easy Auth).
  Not Kratos (that's user-facing only).
- **Tier reality:** prod = Mktg Pro, **Sales Ent**, Service Pro, **Data Ent**, **no Content Hub** →
  in-HubSpot serverless endpoint impossible; custom objects fine. (Confirmed from Products & Add-ons.)
- **Contract:** generic envelope `{ idempotencyKey, objectType, operation, externalId, properties, associations, occurredAt }`. Idempotency = find-or-create + 409-as-success.
- Background: memories `project-middleware-architecture` + `project-hubspot-data-model`; data model
  on Miro `uXjVHKrUyF0=`; portal flow in Figma `bXBtmUduPl32RLhKeVykVw` ("Full Scope – Flow Diagram V4", node `8669:118459`).

## What's BUILT (committed, NOT pushed)

- **HubspotCRMSync** branch `feat/servicebus-producer-azure-adapter`: producer + `/ingest`,
  Service Bus producer + `HubSpotConsumer`, `AppDbContext`+outbox, `HubSpotAdapterClient` (MI bearer),
  `ServiceExtensions`, `nuget.config`. Old PoC files deleted.
- **HubspotApps** branch `feat/hubspot-adapter-function`: `hubspot-adapter-function/` (ingest +
  envelope/mapping/resolve/notes) + `README.md` + `SETUP.md`. JS syntax-checked.

## What's LEFT

1. **Phase 3 — fill the mapping** (`hubspot-adapter-function/src/lib/mapping.js`): *In progress.*
   Done 2026-06-17 from live schema + Miro/Figma: application type id `2-203889439`, property
   `2-203890683`; contact/deal/application/property field maps (deal+contact expanded from the
   Affordability/Additional-Questions screens, flagged provisional). **Blocked:** lead (needs
   `leads-read` scope), offer (no object exists), and `portal_*_id` resolve props missing — see
   spec §7/§16. Live type ids + stage ids captured in the `project-hubspot-data-model` memory.
2. **Build Phase 2.5 — Dubizzle lead intake** (spec §15): `inbound_leads` EF entity (UUIDv7
   PK-as-token, jsonb payload, 60s TTL, one-time `consumed_at`) + `POST /intake/dubizzle`
   (store-only, returns token) + `GET /intake/dubizzle/{token}` (redeem→prefill). Auth on the
   inbound POST still TBD (proposed HMAC). Decisions locked; see §15/§16.
3. **Build/run the .NET side:** GitHub Packages PAT (`read:packages`, SSO for Prypco) → `dotnet
   restore`; create local Postgres `hubspot_sync`; `dotnet ef migrations add InitialOutbox` +
   `database update`; run with `ASPNETCORE_ENVIRONMENT=local`.
4. **Provision Azure** per `hubspot-adapter-function/SETUP.md` (Function App, Entra Easy Auth,
   MI app-role, Key Vault `HUBSPOT_TOKEN`, networking, `hubspot-sync` queue).
5. **Push branches + open PRs** (currently local only).

## Confirm HubSpotDev MCP before Phase 3
The reason for restarting was to load **HubSpotDev MCP**. Confirm it's live (resolve a HubSpot
schema/tool). If yes → pull the live schema and fill `mapping.js` with real values, not TODOs.
If it isn't connected, it won't appear mid-session — restart again.

## First moves on resume
1. Confirm HubSpotDev MCP is connected.
2. If yes → pull schema for lead/deal/application/offer/property → finish `mapping.js` (Phase 3).
3. Then: build .NET (PAT + EF migration), provision Azure (SETUP.md), push + PRs.
4. Keep the .NET side free of any HubSpot property name / type id — those live only in `mapping.js`.
