# Local Testing — Worked Examples

A runnable walkthrough that exercises the service end-to-end locally: a **Bayut** partner lead,
an **organic authenticated** lead, an **anonymous** drop-off, and the **webhook** receiver.
Every command here was run against the app; the "expected" output is what it actually produced.

## What you can test without HubSpot credentials

The `/leads` request path, opportunity-id minting, the **anonymous-hold** logic, and **webhook
signature validation** all run with no real token. The actual outbound *sync* (Contact/Deal
create/update) calls `api.hubapi.com` — with a dummy token those calls return `401` (you'll see
the worker reach HubSpot, retry with back-off, then log the failure). To see them succeed, set a
real **sandbox** token and ensure the custom properties + pipeline exist (plan §10).

| Path | Needs a real token? |
|---|---|
| `POST /leads` → `202` + opportunity id | No |
| Anonymous lead held locally (`held=True`, no HubSpot call) | No |
| Webhook signature validation (200 / 401) + object-type resolution | No (set `HUBSPOT_CLIENT_SECRET`) |
| Contact/Deal create/update, association, open-deal reuse | **Yes** (sandbox token) |
| Inbound mirror actually updating a record (`Mirrored …`) | **Yes** (needs a synced record first) |

## 1. Run the app

```bash
cd HubspotCRMSync
HUBSPOT_CLIENT_SECRET=testsecret \
HUBSPOT_TOKEN=dummy-token \
ASPNETCORE_URLS=http://localhost:5080 \
dotnet run --no-launch-profile
# (swap in the real sandbox HUBSPOT_TOKEN to exercise the outbound sync end-to-end)
```

```bash
curl -s http://localhost:5080/health        # -> {"status":"ok"}
```

## 2. Submit leads

### Bayut partner lead — new deal per unique partnerLeadRef

First submission (new ref → new deal):
```bash
curl -s -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "Bayut",
  "partnerLeadRef": "BYT-99812",
  "email": "sara@example.com",
  "firstName": "Sara", "lastName": "K",
  "phone": "+971501234567",
  "dealName": "Dubai Marina 2BR",
  "amount": 1850000
}'
# -> {"opportunityId":"OPP-<generated>","queued":true}
# Worker: creates contact + deal
```

Same ref again → updates the existing deal (e.g. amount changed):
```bash
curl -s -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "Bayut",
  "partnerLeadRef": "BYT-99812",
  "email": "sara@example.com",
  "amount": 2000000
}'
# Worker: finds deal by partner_lead_ref BYT-99812, PATCHes amount
```

Different ref → new deal for the same contact:
```bash
curl -s -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "Bayut",
  "partnerLeadRef": "BYT-99813",
  "email": "sara@example.com",
  "dealName": "Palm Jumeirah Studio",
  "amount": 950000
}'
# Worker: no deal found for BYT-99813 -> creates a second deal for Sara
```

### Organic, authenticated customer (full flow)

Submit the initial lead — note the `opportunityId` returned:

```bash
curl -s -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "OrganicWeb",
  "isAuthenticated": true,
  "customerId": "CUST-1024",
  "email": "omar@example.com",
  "firstName": "Omar",
  "lastName": "Al-Rashid",
  "phone": "+971501112233",
  "dealName": "JVC Townhouse",
  "amount": 1200000
}'
# -> {"opportunityId":"OPP-abc123","queued":true}
```

Submit with a customer profile snapshot (salary, employment type, debt):

```bash
curl -s -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "OrganicWeb",
  "isAuthenticated": true,
  "customerId": "CUST-1024",
  "opportunityId": "OPP-abc123",
  "customerProfileSnapshot": "{\"salary\":35000,\"employmentType\":\"salaried\",\"isResident\":true,\"totalDebt\":12000,\"currency\":\"AED\"}"
}'
# -> {"opportunityId":"OPP-abc123","queued":true}
# Worker: PATCHes the deal's customer_profile_snapshot field with the JSON string
```

Update the customer profile snapshot (e.g. salary changed, now self-employed):

```bash
curl -s -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "OrganicWeb",
  "isAuthenticated": true,
  "customerId": "CUST-1024",
  "opportunityId": "OPP-abc123",
  "customerProfileSnapshot": "{\"salary\":48000,\"employmentType\":\"self-employed\",\"isResident\":true,\"totalDebt\":8000,\"currency\":\"AED\"}"
}'
# Worker: PATCHes customer_profile_snapshot on the existing deal; dealname and other fields unchanged
```

Update the deal amount (increase) — pass back the `opportunityId`:

```bash
curl -s -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "OrganicWeb",
  "isAuthenticated": true,
  "customerId": "CUST-1024",
  "opportunityId": "OPP-abc123",
  "amount": 1450000
}'
# -> {"opportunityId":"OPP-abc123","queued":true}
# Worker: finds existing contact by customerId, finds existing deal by opportunityId, PATCHes amount
```

Update again (decrease — same pattern, just a lower value):

```bash
curl -s -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "OrganicWeb",
  "isAuthenticated": true,
  "customerId": "CUST-1024",
  "opportunityId": "OPP-abc123",
  "amount": 950000
}'
# -> {"opportunityId":"OPP-abc123","queued":true}
```

The key is always passing back the `opportunityId` — that's how the service finds the existing
deal and PATCHes it instead of creating a new one. Check HubSpot after each call to confirm
the deal's amount field updates in place.

### Anonymous drop-off (retargeting signals)
```bash
curl -s -X POST http://localhost:5080/leads -H 'Content-Type: application/json' -d '{
  "source": "OrganicWeb",
  "anonymousSessionId": "sess-abc123",
  "droppedAt": "offer_selection",
  "offersSeenSnapshot": "ADCB 4.19%, ENBD 4.35%, FAB 4.40%"
}'
# -> {"opportunityId":"OPP-<generated>","queued":true}
```

### What the worker logs

```
Synced OPP-…: contact=(null) deal=(null) held=True      <- anonymous: no HubSpot call, held locally
```

With a **dummy** token, the Bayut + organic leads reach HubSpot and fail auth (proves the path):
```
Received HTTP response headers … - 401         <- real call to api.hubapi.com
Outbox message … failed after 5 attempts        <- retry/back-off, then give up
  at LeadSyncService.ResolveContactAsync(…):line 59   <- Omar: customer-id search first
  at LeadSyncService.ResolveContactAsync(…):line 61   <- Sara: email search
```
With a **real sandbox** token, those become a created/updated Contact + Deal and an association,
and you'd see `Synced OPP-…: contact=<id> deal=<id> held=False`.

> **Note on stages:** `pipelineStage` has been removed from the request model. Deals are created
> in HubSpot without a stage — stage management happens directly in the CRM.

## 3. Webhook receiver (works fully with `HUBSPOT_CLIENT_SECRET`)

Generates a valid v3 signature with the same formula HubSpot uses
(`HMAC-SHA256(method + uri + body + timestamp)`):

```bash
SECRET=testsecret                                # must equal HUBSPOT_CLIENT_SECRET
URI='http://localhost:5080/webhooks/hubspot'
TS=$(date +%s)000
# new generic payload: object.propertyChange + objectTypeId 0-3 (deal)
BODY='[{"eventId":501,"subscriptionType":"object.propertyChange","objectTypeId":"0-3","objectId":987654321,"propertyName":"dealstage","propertyValue":"closedwon","occurredAt":'$TS',"changeSource":"CRM"}]'
SIG=$(printf '%s' "POST${URI}${BODY}${TS}" | openssl dgst -sha256 -hmac "$SECRET" -binary | base64)

curl -s -o /dev/null -w 'HTTP %{http_code}\n' -X POST "$URI" \
  -H "X-HubSpot-Request-Timestamp: $TS" -H "X-HubSpot-Signature-v3: $SIG" -d "$BODY"
# -> HTTP 200
```

Expected results:

| Case | Result |
|---|---|
| Valid signature, fresh timestamp | `200` |
| Tampered body (signature no longer matches) | `401` |
| Timestamp older than 5 minutes | `401` |
| `objectTypeId: "0-3"` | log: `… linked to deals 987654321 …` |
| `objectTypeId: "0-1"` | log: `… linked to contacts 555 …` |

The log line `No local opportunity linked to deals 987654321; nothing to mirror` is expected when
that deal isn't in the local store yet. Once a lead has synced (real token), the same event logs
`Mirrored deals <id>: dealstage=closedwon (state=Closed)` instead — and flips the record to
Closed because `closedwon` is in `HubSpot:ClosedDealStages` (when configured).

Both payload shapes are accepted: the legacy form
(`"subscriptionType":"deal.propertyChange"`, no `objectTypeId`) resolves identically.
