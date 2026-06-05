# HubSpot Integration — Configuration & Operations

Operational companion to the design docs. Covers **every knob the service reads, what it
controls, and how to change it** — including how to update the app's webhook subscriptions.
Read alongside `hubspot-app-and-auth-setup.md` (how the app/token is created) and
`hubspot-integration-plan.md` (why).

> **Audience:** whoever runs or extends this service. If you change a stage name in HubSpot,
> add a webhook, or move to production, the thing you need to touch is in here.

---

## 1. Where configuration lives (three layers)

| Layer | Holds | Changed by |
|---|---|---|
| **Secret env vars** | `HUBSPOT_TOKEN`, `HUBSPOT_CLIENT_SECRET`, `HUBSPOT_BASE_URL` | Secret store / CI; never in source |
| **`appsettings.json` → `HubSpot`** (or env via `HubSpot__…`) | Deal-stage mapping, closed-stage list, logging | Edit the file / set config-style env vars |
| **HubSpot project (`HubspotApps/…`)** | App scopes, **webhook subscriptions**, target URL | Edit `*-hsmeta.json`, `hs project upload`, reinstall |

The three secrets are the only things wired directly to env vars in `Program.cs`. Everything
else binds from the `HubSpot` config section, so it can come from `appsettings.json` **or**
double-underscore env vars (`HubSpot__BaseUrl=…`) in containers.

---

## 2. `appsettings.json` → `HubSpot` reference

```jsonc
{
  "HubSpot": {
    "AccessToken":  "…or HUBSPOT_TOKEN env var (preferred)",
    "ClientSecret": "…or HUBSPOT_CLIENT_SECRET env var (preferred)",
    "BaseUrl":      "https://api.hubapi.com",
    "DealStages":        { },   // canonical stage name -> HubSpot internal stage id
    "ClosedDealStages":  [ ]    // HubSpot internal stage ids that count as Closed
  }
}
```

### `DealStages` — **you almost certainly need to set this**

HubSpot's API does **not** accept human stage labels ("Qualified"); it wants the pipeline's
**internal stage id** (e.g. `appointmentscheduled`, or a generated id like `1098…`). The
portal sends canonical names like `qualified`; this map translates them.

- **Key** = whatever the portal/caller sends in `pipelineStage` (matched case-insensitively).
- **Value** = the HubSpot internal stage id for that stage.
- **Unmapped value → passes through unchanged** and logs a warning. So if a caller already
  sends a real internal id, it still works; if it sends a label with no mapping, HubSpot will
  reject the write — the warning tells you to add the mapping.

Example once you've read the ids off the sandbox pipeline (plan §10 / §3 pipeline):

```jsonc
"DealStages": {
  "new":                    "appointmentscheduled",
  "contacted":              "qualifiedtobuy",
  "qualified":              "presentationscheduled",
  "agreement_in_principle": "decisionmakerboughtin",
  "proceeding":             "contractsent",
  "won":                    "closedwon",
  "lost":                   "closedlost"
}
```

> The right-hand ids above are HubSpot's **default** pipeline ids shown only as a shape
> example — use the **actual** ids from our Mortgage pipeline.

**How to find the internal stage ids:** in HubSpot, *Settings → Objects → Deals → Pipelines*,
pick the Mortgage pipeline, and each stage's internal name is shown (or via the API:
`GET /crm/v3/pipelines/deals`). Record them in plan §10's checklist too.

### `ClosedDealStages` — drives reuse + the inbound mirror

List the internal stage ids that mean the opportunity is **Closed** (Won / Lost / Abandoned).
Two things read it:
- **Open-deal reuse** (outbound): when finding a contact's existing deal to reuse, a deal whose
  stage is in this list is skipped (we'd start a new opportunity instead).
- **Inbound mirror**: a `dealstage` webhook/reconcile that lands on a closed stage flips the
  local record's `State` to `Closed`.

```jsonc
"ClosedDealStages": [ "closedwon", "closedlost" ]
```

**Empty list = every stage is treated as Open** (safe default; nothing auto-closes). The idle
"Abandoned after N days" rule is still an open decision — plan §11 Q4.

---

## 3. What flows which way (so you know what a config change affects)

| Direction | Trigger | HubSpot fields touched |
|---|---|---|
| Outbound (portal → HubSpot) | `POST /leads` → worker | Contact: `portal_customer_id`, `email`, `phone`, `firstname`, `lastname`, `lead_source`. Deal: `opportunity_id`, `dealname`, `dealstage` (mapped), `partner_lead_ref`, `lead_source`, `dropped_at`, `offers_seen_snapshot`, `amount` |
| Inbound (HubSpot → mirror) | webhook + reconcile sweep | **Only HubSpot-owned** props are mirrored: Contact `lifecyclestage`, `hs_lead_status`, `hubspot_owner_id`; Deal `dealstage`, `hubspot_owner_id`. Everything else is ignored |

The inbound allow-list is `HubSpotOwnedFields` in `Sync/LocalMirror.cs`. **If you want HubSpot
to start mastering another field, add it there** — that's the one place that decides what
inbound changes are honoured (the ownership rule, plan §7).

---

## 4. Updating the app's **webhook subscriptions**

On the projects platform, webhook subscriptions are **config-as-code**, not clicks in the UI.
They live in the HubSpot project at `HubspotApps/<project>/src/app/webhooks/webhooks-hsmeta.json`
and deploy with `hs project upload` + a reinstall.

### The subscriptions this service expects

We use the **new generic `crmObjects`** subscription format (consistent with the projects
platform; the `legacyCrmObjects` block is the deprecated path). Subscribe to exactly the
HubSpot-owned fields the mirror consumes (§3):

| Subscription (`objectType` · `subscriptionType` · `propertyName`) | Why |
|---|---|
| `contact` · `object.creation` | new contacts created in HubSpot |
| `contact` · `object.propertyChange` · `lifecyclestage` | funnel progression |
| `contact` · `object.propertyChange` · `hs_lead_status` | qualification status |
| `contact` · `object.propertyChange` · `hubspot_owner_id` | rep ownership |
| `deal` · `object.creation` | deals created in HubSpot |
| `deal` · `object.propertyChange` · `dealstage` | top-of-funnel stage moves |
| `deal` · `object.propertyChange` · `hubspot_owner_id` | rep ownership |

**Payload shapes — the service handles both.** The generic payload sends
`subscriptionType: "object.propertyChange"` plus an `objectTypeId` (`0-1` contact, `0-3` deal);
the legacy payload encodes the type in `subscriptionType` (`deal.propertyChange`).
`Models.cs::WebhookEvent` + `InboundEventProcessor.ResolveObjectType` parse either, so you can
switch subscription formats without touching code. Mapping lives in `ResolveObjectType` — add
an `objectTypeId` there if you ever subscribe to another object (e.g. company `0-2`).

> ⚠️ **Validate before upload.** Webhook hsmeta field names are platform-version-specific. The
> CLI scaffold for this project's `platformVersion` (2026.03) uses `objectType` inside
> `crmObjects` (some HubSpot docs show `objectName` for other versions). Run
> **`hs project validate`** before `hs project upload`, and confirm the real delivered payload
> against the sandbox before flipping to prod.

### The steps to change a subscription

1. Edit `HubspotApps/<project>/src/app/webhooks/webhooks-hsmeta.json`:
   - set `config.settings.targetUrl` to the service's public HTTPS endpoint **ending in
     `/webhooks/hubspot`** (a tunnel like ngrok/cloudflared in dev; the real host in prod);
   - add/remove subscription entries; set `"active": true` to turn one on.
2. `hs project upload` (deploys the project to the connected account).
3. **Reinstall / update the app** so the new subscriptions take effect:
   *Development → Projects → SyncApp → Distribution → Install (or "Update")*.
4. Confirm deliveries in HubSpot's webhook logs and that the service returns `200` fast
   (HubSpot expects an ack within ~5s; the service acks then processes async).

Signature validation needs no change — it always uses `HUBSPOT_CLIENT_SECRET` (the app client
secret) regardless of which subscriptions are active.

---

## 5. Account-side prerequisites (custom properties + pipeline)

Create these **before** the first sync, or create/update calls reject unknown properties
(full checklist in plan §10):

- **Contact:** `portal_customer_id` (unique if the tier allows), `lead_source`.
- **Deal:** `opportunity_id` (unique), `partner_lead_ref`, `dropped_at`, `offers_seen_snapshot`.
- **Mortgage pipeline** + stages; record each stage's **internal id** and put them in
  `DealStages` / `ClosedDealStages` (§2).
- Standard HubSpot props the mirror reads (`lifecyclestage`, `hs_lead_status`, `dealstage`,
  `hubspot_owner_id`) exist out of the box.

---

## 6. Per-environment summary (what differs sandbox → prod)

| Setting | Sandbox | Production |
|---|---|---|
| `HUBSPOT_TOKEN` / `HUBSPOT_CLIENT_SECRET` | sandbox app install | prod app install (separate token) |
| `HUBSPOT_BASE_URL` | `https://api.hubapi.com` | same |
| Webhook `targetUrl` | dev tunnel → `…/webhooks/hubspot` | prod host → `…/webhooks/hubspot` |
| `DealStages` / `ClosedDealStages` | sandbox pipeline ids | prod pipeline ids (re-read; ids differ per portal) |
| Stores | in-memory (PoC) | DB-backed (see README "Seams to replace") |

Portal IDs and account names are in `hubspot-app-and-auth-setup.md` §"Sandbox vs production".

---

## 7. What changed in the latest build (B-series fixes)

These three items from the README roadmap / plan are now implemented:

- **Deal-stage mapping (`DealStageMap`).** `pipelineStage` is translated to the HubSpot
  internal stage id via `HubSpot:DealStages` before any create/update; unmapped values pass
  through with a warning. Fixes silent stage-write failures. *Config: §2.*
- **Inbound mirror (`LocalMirror`).** The webhook processor and reconciliation sweep now
  actually update the local opportunity record for HubSpot-owned fields (was log-only),
  honouring the ownership allow-list and a last-writer (`occurredAt`) check. *Config: §3.*
- **Find-open-deal via HubSpot associations.** When the local store has no record (e.g. after
  a restart, or an organic lead with no customer id), the service now asks HubSpot for the
  contact's associated deals and reuses an open one (`ClosedDealStages` decides "open") instead
  of always creating a duplicate. *Config: §2 `ClosedDealStages`.*

- **Webhook payload: new generic `crmObjects` format.** Subscriptions moved to the generic
  format and `InboundEventProcessor.ResolveObjectType` now resolves the object from `objectTypeId`
  (`0-1`/`0-3`), falling back to the legacy `subscriptionType`. Both shapes are accepted. *Config: §4.*

Still open / not built: Pulse handoff (phase 5), anonymous→known stitching (§11 Q5),
retargeting mechanism (§11 Q1), and DB-backed stores. See README roadmap.

For a runnable, end-to-end local walkthrough (organic + Bayut + anonymous + webhook, with
expected output), see `local-testing.md`.
