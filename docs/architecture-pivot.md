# Architecture Pivot — Webhook-Driven Updates & CRM Cards

Two changes from the original design, documented here before implementation.

---

## 1. Webhook-Driven Updates (Portal → Sync Service → HubSpot)

### What changes

| | Before | After |
|---|---|---|
| Interface | Portal calls `POST /leads` (REST) | Portal fires a webhook event to the sync service |
| Trigger | Synchronous request/response | Event-driven, fire-and-forget from portal's perspective |
| Processing | Same transactional outbox pattern | Same — webhook enqueues to outbox, worker syncs to HubSpot |

HubSpot remains the source of truth. The sync service is still the intermediary — it receives events from the portal and calls HubSpot's CRM REST API to create/update records. The only thing that changes is the interface between the portal and the sync service.

### New flow

```
Customer updates profile in portal
        │
        ▼
Portal fires webhook event
  POST /events
  Headers: X-Portal-Signature: <hmac>
  Body: { "type": "lead.updated", "payload": { ... } }
        │
        ▼
Sync service validates signature, enqueues to outbox
        │
        ▼
OutboxWorker picks up, calls HubSpot CRM API
  PATCH /crm/v3/objects/contacts/{id}
  PATCH /crm/v3/objects/deals/{id}
```

### Event types to handle

| Event type | Trigger | HubSpot action |
|---|---|---|
| `lead.created` | New lead starts the flow | Create contact + deal |
| `lead.updated` | Customer updates profile/amount | Patch existing contact/deal |
| `lead.identified` | Anonymous session → logged in | Stitch + create in HubSpot |
| `lead.dropped` | Customer exits at a step | Update `dropped_at` + `offers_seen_snapshot` on deal |

### Signature validation

The portal signs each event with a shared secret (stored as `PORTAL_WEBHOOK_SECRET` env var).
The sync service validates before processing:

```
HMAC-SHA256(secret, timestamp + "." + body)
```

Reject if timestamp is older than 5 minutes (replay protection).

### Endpoint design

```
POST /events
Headers:
  X-Portal-Signature: <hmac>
  X-Portal-Timestamp: <unix-ms>
Body: { "type": "lead.updated", "payload": { <LeadSyncRequest fields> } }

Response: 200 OK (ack fast; process async via outbox)
```

The payload shape mirrors `LeadSyncRequest` so the existing sync logic is reused with
minimal changes — the endpoint just validates, wraps in an `OutboxMessage`, and returns.

---

## 2. External Data via HubSpot CRM Cards

### The problem

Bank products, mortgage offers, and other external data live in systems we don't own.
We don't want to copy and maintain that data in HubSpot — it goes stale and creates a
second source of truth.

### The solution: store the reference, fetch the data on demand

```
HubSpot Deal
  ├── bank_product_id: "PROD-7821"      ← just the pointer, stored in HubSpot
  └── [CRM Card in sidebar]
          │ on load, reads bank_product_id
          ▼
      Sync service /cards/bank-product?dealId=...&bankProductId=PROD-7821
          │ calls external bank API
          ▼
      Returns formatted data → HubSpot renders in card
```

The sales rep sees live bank product data (rates, terms, LTV, eligibility) in the HubSpot
deal sidebar without that data ever being stored in HubSpot.

### How HubSpot CRM Cards work

HubSpot CRM Cards (Private App feature, no Projects platform needed) work as follows:

1. You register a card in the Private App — give it a title, which objects it appears on
   (e.g. Deal), and a **data fetch URL** on your backend.
2. When a sales rep opens a deal, HubSpot calls your data fetch URL with:
   - The CRM record's properties (including any you specify, e.g. `bank_product_id`)
   - A user token scoped to that request
3. Your backend fetches the real data from the external system using the ID from the CRM
   properties, and returns a structured JSON response.
4. HubSpot renders the response as a card in the sidebar — labels, values, links, buttons.

No iframe, no frontend code to write. Your backend returns JSON; HubSpot does the rendering.

### Card backend endpoint

```
GET /cards/bank-product
Query params supplied by HubSpot:
  portalId=148631333
  userId=<hs-user-id>
  associatedObjectId=<deal-hs-id>
  associatedObjectType=DEAL
  bank_product_id=PROD-7821        ← property you declared in card config

Response shape (HubSpot CRM Card format):
{
  "results": [
    {
      "objectId": "PROD-7821",
      "title": "ADCB Home Finance — Fixed 4.19%",
      "properties": [
        { "label": "Rate", "dataType": "STRING", "value": "4.19%" },
        { "label": "LTV", "dataType": "STRING", "value": "80%" },
        { "label": "Max Term", "dataType": "STRING", "value": "25 years" },
        { "label": "Min Salary", "dataType": "STRING", "value": "AED 15,000" }
      ],
      "actions": [
        {
          "type": "IFRAME",
          "label": "View full product sheet",
          "width": 890,
          "height": 748,
          "uri": "https://your-service/product-details/PROD-7821"
        }
      ]
    }
  ]
}
```

### HubSpot properties to add (per object)

**Deal:**
| Property | Type | Purpose |
|---|---|---|
| `bank_product_id` | Single-line text | Reference to the matched bank product |
| `bank_product_source` | Single-line text | Which bank/aggregator the product came from |

### Security — validating the card request

HubSpot signs every card data fetch request with the Private App's client secret (same
`HUBSPOT_CLIENT_SECRET`). Validate using the v3 signature before calling the external system:

```
HMAC-SHA256(clientSecret, requestMethod + uri + body + timestamp)
```

This is the same validation logic as HubSpot webhooks — the same `WebhookSignatureValidator`
class can be reused.

### What lives where

| Data | Lives in | Why |
|---|---|---|
| `bank_product_id` (the pointer) | HubSpot Deal property | HubSpot is source of truth for which product is matched |
| Product rates, terms, LTV, eligibility | External bank system / aggregator | We don't own it; it changes frequently |
| Which card to show | Private App card config | One-time setup, no deploy needed to change data |
| Card rendering | HubSpot (from our JSON response) | No frontend to build or host |

### Extending to other external systems

The same pattern applies to anything else you don't want to replicate:
- Mortgage calculator results — store `calculator_session_id`, fetch on demand
- Credit bureau data — store `bureau_ref`, fetch on demand
- Partner (Bayut/Dubizzle) lead details — store `partner_lead_ref`, fetch on demand

Each gets its own CRM Card and its own backend endpoint. The card config in HubSpot declares
which properties to pass through; the backend decides how to fetch and format the response.

---

## Implementation order

1. **Webhook receiver** — `POST /events` endpoint + signature validation + outbox enqueue
2. **Remove** `POST /leads` (or keep temporarily while portal migrates)
3. **Bank product CRM Card** — add `bank_product_id` property to Deal, register card in
   Private App, build `/cards/bank-product` endpoint
4. **Additional cards** as external data sources are identified

---

## Open decisions

- **Portal webhook secret** — agree on the shared secret and rotation process with the portal team
- **External bank API** — what's the API shape? Does it require auth? Rate limits?
- **Card object placement** — should the bank product card live on Deal, or on a custom Application object?
- **Multiple products per deal** — a deal might match multiple bank products; the card response supports multiple `results` so this is handled, but the property model needs to store multiple IDs (comma-separated, or a custom object with one row per product)
