# HubSpot Integration — Configuration & Operations

Operational companion to the design docs. Covers **every knob the service reads, what it
controls, and how to change it** — including how to update the app's webhook subscriptions.
Read alongside `hubspot-app-and-auth-setup.md` (how the app/token is created) and
`hubspot-integration-plan.md` (why).

> **Audience:** whoever runs or extends this service. If you change a stage name in HubSpot,
> add a webhook, or move to production, the thing you need to touch is in here.

---

## 1. Where configuration lives

| Layer | Holds | Changed by |
|---|---|---|
| **Secret env vars** | `HUBSPOT_TOKEN`, `HUBSPOT_CLIENT_SECRET`, `HUBSPOT_BASE_URL` | Secret store / CI; never in source |
| **`appsettings.json` → `HubSpot`** (or env via `HubSpot__…`) | Deal-stage mapping, closed-stage list, `ApplicationObjectTypeId`, logging | Edit the file / set config-style env vars |
| **HubSpot Private App UI** | App scopes, webhook subscriptions, target URL | Settings → Integrations → Private Apps |

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

## 4. Webhook subscriptions (if/when needed)

Webhooks are not currently active. When you need them, configure them directly in the Private App:

**Settings → Integrations → Private Apps → your app → Webhooks tab**

Set the target URL to the service's public HTTPS endpoint ending in `/webhooks/hubspot`. In dev,
use [ngrok](https://ngrok.com) to expose localhost:

```bash
ngrok http 5080
# -> your target URL: https://<id>.ngrok-free.app/webhooks/hubspot
```

Update the target URL in the Private App UI whenever the ngrok URL changes (each session on a free plan). A paid ngrok plan lets you reserve a static subdomain.

### Adding or changing scopes

**Settings → Integrations → Private Apps → your app → Scopes tab** → add scope → **Update app** → rotate token → update `appsettings.json`.

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

### Required scopes

The private app **must** have these scopes deployed via `hs project upload` (not just set in the UI):

| Scope | Why |
|---|---|
| `crm.objects.custom.read` | Read any custom object record (Application, etc.) |
| `crm.objects.custom.write` | Create/update custom object records |
| `crm.schemas.custom.read` | Read the custom object schema/property definitions |

These are already in `app-hsmeta.json`. They take effect only after a **successful** `hs project upload`, a reinstall via the distribution action, and a token rotation — adding them in the HubSpot UI alone is not enough when the app is managed via a project.

**Full deploy sequence every time scopes or subscriptions change:**

1. `hs project upload` — deploys the updated config to HubSpot
2. In HubSpot: *Development → Projects → TestCRMSync → Distribution → Install / Update* — reinstalls the app so the new scopes are applied
3. Rotate the private app token: *Settings → Integrations → Private Apps → your app → Rotate token*
4. Update `appsettings.json` with the new token

Skipping step 2 means the old token's scopes are still in effect even after a successful upload.

### Deploying scopes — common blockers

`hs project upload` will refuse to deploy if it detects that existing webhook subscriptions would be removed. This happens when HubSpot's backend knows about subscriptions from a previous deploy that are no longer in `webhooks-hsmeta.json`.

**Fix:** add the missing subscriptions back as `"active": false` so there are no removals.

The `webhooks-hsmeta.json` supports two subscription formats side by side:

- `crmObjects` — the current format. Valid `subscriptionType` values: `object.creation`, `object.deletion`, `object.merge`, `object.restore`, `object.propertyChange`, `object.associationChange`. Note: `privacy.deletion` is **not** valid here.
- `legacyCrmObjects` — the old format. Use this to retain previously-deployed legacy subscriptions (e.g. `contact.privacyDeletion`, `contact.deletion`). Set them to `"active": false` if they are no longer needed.

Both blocks can coexist in the same file. Keep inactive legacy entries in the file permanently to avoid future removal warnings.

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
