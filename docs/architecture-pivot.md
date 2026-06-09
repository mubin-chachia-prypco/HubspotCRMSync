# Architecture Pivot — HubSpot Workflow Webhooks & CRM Cards

Two decisions that replace the original REST-API-push approach.

---

## 1. HubSpot Workflow Webhook Triggers (pushing data into HubSpot)

### The idea

Instead of calling HubSpot's CRM REST API to create/update records directly, we POST to a
**HubSpot-provided webhook URL**. HubSpot receives the payload, a Workflow fires, and HubSpot
updates its own records internally. We don't touch the API — HubSpot does the work.

### How it works

1. **HubSpot team** creates Workflows with "Webhook" as the enrollment trigger and sends us the URLs.
2. **Our service** POSTs a JSON payload to the right URL when something changes.
3. HubSpot's workflow fires, maps the payload fields to CRM properties, and does the rest.

```
Customer updates profile in portal
        │
        ▼
Our service
  POST <hubspot-workflow-webhook-url>
  { "email": "...", "opportunity_id": "...", "amount": 1450000, ... }
        │
        ▼
HubSpot Workflow (their responsibility)
  → find/create contact, update deal, trigger automations
```

### Responsibility split

| | Our service | HubSpot team |
|---|---|---|
| Workflow setup | ✗ | ✓ — build workflows, configure field mapping |
| Webhook URLs | Receive and store in config | ✓ — provide one URL per workflow |
| Payload format | ✓ — agree and POST correctly | ✓ — map fields in workflow |
| CRM dedup/upsert logic | ✗ | ✓ — handled inside the workflow |
| Downstream automations | ✗ | ✓ — notifications, stage moves, assignments |

Our service has no knowledge of HubSpot internals — just a URL and a payload shape per event type.

### Webhook URLs (add when provided by HubSpot team)

Store in config / environment variables — never hardcode:

```json
"HubSpot": {
  "WorkflowWebhooks": {
    "LeadCreated":          "<url>",
    "LeadUpdated":          "<url>",
    "LeadIdentified":       "<url>",
    "LeadDropped":          "<url>"
  }
}
```

### Payload shape

Keep it flat and explicit — HubSpot workflow field mapping works best with simple key-value pairs:

```json
{
  "email": "omar@example.com",
  "first_name": "Omar",
  "last_name": "Al-Rashid",
  "phone": "+971501112233",
  "portal_customer_id": "CUST-1024",
  "opportunity_id": "OPP-abc123",
  "partner_lead_ref": "BYT-99812",
  "lead_source": "Bayut",
  "deal_name": "Dubai Marina 2BR",
  "amount": 1850000,
  "customer_profile_snapshot": "{\"salary\":35000,\"employmentType\":\"salaried\"}",
  "dropped_at": "offer_selection",
  "offers_seen_snapshot": "ADCB 4.19%, ENBD 4.35%"
}
```

### Open questions (to align with HubSpot team)

- One workflow per event type, or one workflow that branches on `type`?
- Exact field names they expect in the payload — agree before building
- Do they need `opportunity_id` in every payload to find the right deal?

---

## 2. External Data via HubSpot CRM Cards

### The problem

Bank products, mortgage offers, and other external data live in systems we don't own.
Copying it into HubSpot creates a second source of truth that goes stale.

### The solution: store the reference, fetch the data on demand

```
HubSpot Deal
  ├── bank_product_id: "PROD-7821"      ← just the pointer, stored in HubSpot
  └── [CRM Card in deal sidebar]
          │ on load, reads bank_product_id from the deal
          ▼
      Your service: GET /cards/bank-product?bankProductId=PROD-7821
          │ calls external bank API / aggregator
          ▼
      Returns formatted data → HubSpot renders it in the card
```

The sales rep sees live bank product data (rate, LTV, term, eligibility) in the deal sidebar.
That data never touches your database.

### How CRM Cards work

CRM Cards are a Private App feature — no Projects platform or CLI needed.

1. Register the card in the Private App settings — give it a name, pick which object it
   appears on (Deal), and provide a **data fetch URL** on your backend.
2. Declare which CRM properties to pass through (e.g. `bank_product_id`, `opportunity_id`).
3. When a rep opens a deal in HubSpot, HubSpot calls your data fetch URL with those
   property values as query params.
4. Your endpoint fetches the real data using the ID, and returns a structured JSON response.
5. HubSpot renders it as a card — no frontend to write.

### Card backend endpoint

```
GET /cards/bank-product
Query params (sent by HubSpot):
  portalId=148631333
  associatedObjectId=<deal-hs-id>
  associatedObjectType=DEAL
  bank_product_id=PROD-7821          ← declared in card config
  opportunity_id=OPP-abc123

Response (HubSpot CRM Card format):
{
  "results": [
    {
      "objectId": "PROD-7821",
      "title": "ADCB Home Finance — Fixed 4.19%",
      "properties": [
        { "label": "Rate",       "dataType": "STRING", "value": "4.19%" },
        { "label": "LTV",        "dataType": "STRING", "value": "80%" },
        { "label": "Max Term",   "dataType": "STRING", "value": "25 years" },
        { "label": "Min Salary", "dataType": "STRING", "value": "AED 15,000" }
      ]
    }
  ]
}
```

### HubSpot properties needed on Deal

| Property | Type | Purpose |
|---|---|---|
| `bank_product_id` | Single-line text | Reference to the matched bank product |
| `bank_product_source` | Single-line text | Which bank/aggregator the product came from |

### Validating the card request

HubSpot signs every card data fetch request with your Private App's client secret.
Validate using the v3 signature before calling the external system — same logic as
webhook validation (`HMAC-SHA256(clientSecret, method + uri + body + timestamp)`).

### Extending to other external systems

The same pattern applies to anything you don't want to replicate in HubSpot:

| External data | ID stored in HubSpot | Card endpoint |
|---|---|---|
| Bank product | `bank_product_id` on Deal | `/cards/bank-product` |
| Mortgage calculator result | `calculator_session_id` on Deal | `/cards/calculator` |
| Credit bureau report | `bureau_ref` on Contact | `/cards/bureau` |
| Partner lead details | `partner_lead_ref` on Deal | `/cards/partner-lead` |

Each card is one registration in the Private App and one endpoint in your service.

### Open questions

- **Multiple products per deal** — if a deal can match several bank products, the card
  `results` array supports multiple items, but HubSpot only stores one `bank_product_id`.
  Options: comma-separated IDs in the property, or a custom `MatchedProduct` object with
  one row per product associated to the deal.
- **Which object does the card live on** — Deal, or the Application custom object?
- **Bank API auth** — what credentials does the external bank/aggregator API require?

---

## Implementation order

1. Set up HubSpot Workflow Webhook Triggers (in HubSpot UI — no code)
2. Build `POST /events` endpoint in the service — validate, format, relay to the right workflow URL
3. Add `bank_product_id` property to Deal in HubSpot
4. Register the CRM Card in the Private App UI
5. Build `/cards/bank-product` endpoint
6. Add more cards as external data sources are confirmed
