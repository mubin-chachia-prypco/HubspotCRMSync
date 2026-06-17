# Resume / Handoff Note

Short, restart-friendly note so a fresh Claude Code session (or a teammate) can pick this up
exactly where we left off. **Read this first, then `docs/forwarder-adapter-spec.md`.**

_Last updated: 2026-06-17_

---

## Why you might be restarting

To load the **HubSpotDev MCP** server. It was installed mid-session and is **not reachable in the
session it was added to** — Claude Code only loads MCP servers at startup. **Restart Claude Code /
reload the window**, then confirm it's connected before Phase 3 (below).

**Confirm HubSpotDev is live:** run a tool search for it (e.g. ask for the HubSpot schema). If a
`mcp__*hubspot*` tool resolves, you're good. If not, it isn't connected yet — check the MCP config.

---

## Where we are

- **Plan is signed off.** Architecture, contract, phases: `docs/forwarder-adapter-spec.md`.
- **Decisions (all locked):**
  - Two-piece middleware: **.NET forwarder** (`HubspotCRMSync`, this repo) + **Cloudflare Worker
    adapter** (`HubspotApps/adapter-worker/`, JS).
  - In-HubSpot serverless **dropped** — no Content Hub Enterprise (tiers: Mktg Pro, **Sales Ent**,
    Service Pro, **Data Ent**). Custom objects are fine.
  - Generic **envelope** contract; **find-or-create** idempotency (no key store); **mapping lives
    in the adapter**.
- **Nothing built yet** — code work starts at Phase 1.
- Background: memories `project-middleware-architecture` + `project-hubspot-data-model`; data model
  on Miro board `uXjVHKrUyF0=`; portal flow in Figma `bXBtmUduPl32RLhKeVykVw` ("Full Scope – Flow
  Diagram V4", node `8669:118459`).

## What needs no restart (can build now)

- **Phase 1 — Worker skeleton** (`HubspotApps/adapter-worker/`): wrangler + package + `index.js`;
  shared-secret gate; envelope parse/validate; `contact` upsert by `externalId` (port Juan's fn).
  Add `crm.objects.notes.write` to SyncApp `app-hsmeta.json` + reinstall.
- **Phase 2 — Forwarder rewrite** (`HubspotCRMSync`): `POST /ingest` envelope ingress; worker POSTs
  to the Worker; add `IDeadLetterQueue` + `IAdapterClient`; delete `HubSpot/HubSpotClient.cs` +
  mapping in `Sync/LeadSyncService.cs`; config `Forwarder: { AdapterIngestUrl, IngestSharedSecret }`.

## What is blocked until HubSpotDev MCP is connected

- **Phase 3 — Full mapping** (`mapping.js`, `resolve.js`): needs exact **custom-object type ids**
  + **property names** from the live sandbox schema. Use HubSpotDev MCP (or `GET /crm/v3/schemas`)
  to fill the TODOs. Don't guess these.

## Waiting on Mubin (external)

- Cloudflare account + secrets: `HUBSPOT_TOKEN`, `INGEST_SHARED_SECRET`, Worker route/domain.
- Per-screen field lists with FE (or read each Figma frame on demand).

## First moves on resume

1. Confirm HubSpotDev MCP is connected (above).
2. If yes → pull live schema, finish Phase 3 mapping. If still doing Phase 1/2 → start there (no MCP needed).
3. Keep the .NET side free of any HubSpot property name / type id — those live only in the Worker.
