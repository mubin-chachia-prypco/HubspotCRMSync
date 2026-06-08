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
    "DealStages":              { },          // canonical stage name -> HubSpot internal stage id
    "ClosedDealStages":        [ ],          // HubSpot internal stage ids that count as Closed
    "ApplicationObjectTypeId": "2-xxxxxxx"  // HubSpot custom object type id for Applications
  }
}
```

### `DealStages` — **not currently used; reserved for future stage sync**

`pipelineStage` has been removed from the `LeadSyncRequest` model. Deals are pushed to HubSpot
without a stage — the CRM receives the deal and stage is managed directly in HubSpot. `DealStages`
remains in config as a reserved hook if outbound stage mapping is reintroduced later (e.g. driven
by the customer portal). Leave it as an empty object for now.

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
| Outbound (portal → HubSpot) | `POST /leads` → worker | Contact: `portal_customer_id`, `email`, `phone`, `firstname`, `lastname`, `lead_source`. Deal: `opportunity_id`, `dealname`, `partner_lead_ref`, `lead_source`, `customer_profile_snapshot`, `dropped_at`, `offers_seen_snapshot`, `amount` |
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

### Exposing the local service to HubSpot via ngrok

HubSpot webhooks require a **public HTTPS endpoint** — they can't reach `localhost`. In
development, use [ngrok](https://ngrok.com) to tunnel to your local service.

#### One-time setup

1. Install ngrok: `brew install ngrok/ngrok/ngrok` (or download from ngrok.com).
2. Authenticate once: `ngrok config add-authtoken <your-token>` (free account is sufficient).

#### Each dev session

```bash
# 1. Start the service (in one terminal)
HUBSPOT_CLIENT_SECRET=<your-secret> \
HUBSPOT_TOKEN=<your-token> \
ASPNETCORE_URLS=http://localhost:5080 \
dotnet run --no-launch-profile

# 2. Start the tunnel (in another terminal)
ngrok http 5080
```

ngrok prints a public URL like `https://a1b2-203-0-113-1.ngrok-free.app`.  
Your webhook target URL is: `https://a1b2-203-0-113-1.ngrok-free.app/webhooks/hubspot`

> **Note:** The ngrok URL changes every session on a free plan. You'll need to update
> `targetUrl` in the hsmeta config and re-upload each time. A paid ngrok plan lets you reserve a
> static subdomain to avoid this.

#### Update the webhook target URL

After you have the ngrok URL, update the hsmeta file and redeploy:

```bash
# Edit the targetUrl in:
#   HubspotApps/<project>/src/app/webhooks/webhooks-hsmeta.json
# Then:
hs project upload
# Reinstall via HubSpot UI: Development → Projects → SyncApp → Distribution → Update
```

Confirm it's working:

```bash
# HubSpot webhook delivery logs: Settings → Integrations → Private Apps → your app → Webhooks → Recent deliveries
# Or watch your dotnet terminal — a valid delivery logs the event type and object id.
```

---

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
(full checklist in plan §10).

To create a property: **Settings → Properties**, select the object, click **Create property**.
The **internal name** must match exactly — the label is just display text.

### Contact properties

| Internal name | Type | Why we need it |
|---|---|---|
| `portal_customer_id` | Single-line text | Your portal's internal customer ID (e.g. `CUST-1024`). HubSpot has no native slot for this. The service uses it to look up an existing contact by your ID on repeat submissions, avoiding duplicates even when email/phone changes. |
| `lead_source` | Single-line text | Which channel the lead came from (`OrganicWeb`, `Bayut`, etc.). HubSpot's built-in `hs_lead_source` is a fixed enum with generic values ("Paid Search") that don't map to our partners — this custom field carries our actual source labels. |

### Deal properties

| Internal name | Type | Why we need it |
|---|---|---|
| `lead_source` | Single-line text | Same as the contact-level field — records which channel originated the deal. Must be created on **both** the Contact and Deal objects; HubSpot properties are per-object and don't carry over. |
| `customer_profile_snapshot` | Multi-line text | Raw JSON blob from the customer portal capturing applicant profile data at the time of submission — salary, employment type (salaried/self-employed/resident), debt obligations, etc. Stored as-is; not filterable in HubSpot but gives the sales team full context on the record. Schema evolves with the portal; no migration needed when fields are added. |
| `opportunity_id` | Single-line text | Your internal opportunity reference (e.g. `OPP-abc123`). **Critical** — this is how the service finds and updates an existing deal on subsequent calls. Without it, every lead submission creates a duplicate deal. |
| `partner_lead_ref` | Single-line text | The partner's own reference number (e.g. Bayut's `BYT-99812`). Stored for support queries and cross-system reconciliation. |
| `dropped_at` | Single-line text | Where in the funnel an anonymous user dropped off (e.g. `offer_selection`). Carries retargeting signal for re-engagement campaigns. |
| `offers_seen_snapshot` | Single-line text | Snapshot of mortgage offers the user saw before dropping off (e.g. `"ADCB 4.19%, ENBD 4.35%"`). Paired with `dropped_at` for retargeting context. |

### Pipeline

- **Mortgage pipeline** — create the pipeline in HubSpot. Record closed-stage internal ids in
  `ClosedDealStages` (§2). `DealStages` is currently unused (see §2).
- Standard HubSpot props the mirror reads (`lifecyclestage`, `hs_lead_status`, `dealstage`,
  `hubspot_owner_id`) exist out of the box — no need to create them.

---

## 6. Applications custom object

The service supports a one-deal-to-many-applications relationship via a HubSpot custom object.

### Setup (one-time, per HubSpot account)

1. **Create the custom object** — Settings → Objects → Custom Objects → Create custom object.
   Name it `Application` and add whatever properties you need (applicant name, status, income, etc.).
2. **Define the association** — on the custom object, go to the Associations tab and add an
   association to **Deal**.
3. **Find the object type ID** — HubSpot assigns a type id like `2-203884532` (visible in the
   URL when viewing the object's property settings: `…/properties?type=2-xxxxxxx`).
4. **Set `ApplicationObjectTypeId` in config:**
   ```json
   "ApplicationObjectTypeId": "2-203884532"
   ```
   This value differs between sandbox and production portals — update it per environment.
5. **Add the required scopes** to your private app — Settings → Integrations → Private Apps →
   your app → Scopes:
   - `crm.objects.custom.read`
   - `crm.objects.custom.write` (needed when the service creates application records)

### How the query works

`GET /deals/{opportunityId}` makes two parallel HubSpot calls:
1. Fetch the deal record with all properties (`GET /crm/v3/objects/deals/{id}?allProperties=true`)
2. Get associated application IDs (`GET /crm/v4/objects/deals/{id}/associations/{applicationTypeId}`)

Then fetches each application record individually with all its properties, and returns everything
in a single JSON response.

> **HubSpot doesn't support joins** — there is no single API call that returns a deal with its
> applications embedded. Two round-trips is the minimum; this is a platform constraint, not a
> service limitation.

### Per-environment config

| Setting | Sandbox | Production |
|---|---|---|
| `ApplicationObjectTypeId` | sandbox portal type id | prod portal type id (re-read from URL) |

---

## 7. Per-environment summary (what differs sandbox → prod)

| Setting | Sandbox | Production |
|---|---|---|
| `HUBSPOT_TOKEN` / `HUBSPOT_CLIENT_SECRET` | sandbox app install | prod app install (separate token) |
| `HUBSPOT_BASE_URL` | `https://api.hubapi.com` | same |
| Webhook `targetUrl` | dev tunnel → `…/webhooks/hubspot` | prod host → `…/webhooks/hubspot` |
| `DealStages` / `ClosedDealStages` | sandbox pipeline ids | prod pipeline ids (re-read; ids differ per portal) |
| Stores | in-memory (PoC) | DB-backed (see README "Seams to replace") |

Portal IDs and account names are in `hubspot-app-and-auth-setup.md` §"Sandbox vs production".

---

## 8. What changed in the latest build (B-series fixes)

These three items from the README roadmap / plan are now implemented:

- **`pipelineStage` removed from `LeadSyncRequest`.** Deals are created in HubSpot without a
  stage; stage management happens directly in the CRM. `DealStages` config is reserved but
  unused. *Config: §2.*
- **Inbound mirror (`LocalMirror`).** The webhook processor and reconciliation sweep now
  actually update the local opportunity record for HubSpot-owned fields (was log-only),
  honouring the ownership allow-list and a last-writer (`occurredAt`) check. *Config: §3.*
- **Source-aware deal resolution.** Each source has its own dedup key — no blind reuse of open
  deals across unrelated journeys:
  - **`opportunityId` provided** → always update that specific deal (all sources).
  - **Partner source** (`Bayut`, `Dubizzle`, etc.) **+ `partnerLeadRef`** → dedup by the
    partner's reference; same ref updates the deal, new ref creates a new one.
  - **Organic** (`OrganicWeb`, `OrganicApp`) **with no `opportunityId`** → always create a new
    deal; each new journey start is a distinct opportunity.

- **Webhook payload: new generic `crmObjects` format.** Subscriptions moved to the generic
  format and `InboundEventProcessor.ResolveObjectType` now resolves the object from `objectTypeId`
  (`0-1`/`0-3`), falling back to the legacy `subscriptionType`. Both shapes are accepted. *Config: §4.*

- **Anonymous→known stitching.** When a request includes both an `anonymousSessionId` and
  identity (email/customerId), the service looks up the held anonymous record, reuses its
  `opportunityId`, and merges its retargeting signals (`droppedAt`, `offersSeenSnapshot`) into
  the new request before syncing to HubSpot. The resulting deal carries the full context from
  both the anonymous and authenticated submissions.

Still open / not built: Pulse handoff (phase 5), retargeting mechanism (§11 Q1), and
DB-backed stores. See README roadmap.

For a runnable, end-to-end local walkthrough (organic + Bayut + anonymous + webhook, with
expected output), see `local-testing.md`.
