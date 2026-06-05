# HubSpot Integration — Concepts & Architecture

Mermaid diagrams for (1) how HubSpot models the lead → deal → application flow, and
(2) how to architect the two-way sync between the customer portal and HubSpot.

---

## 1. The HubSpot object model (the mental model)

Unlike Salesforce, nothing "converts". The **Contact is the person and lives
forever**; a **Deal** is one opportunity attached to it. In HubSpot the deal covers the
**top-of-funnel** stages; the application itself runs in **PRYPCO Pulse** (only a coarse
outcome comes back to the deal). One person can have many deals over time, and one deal
can have many contacts (joint applicants).

```mermaid
flowchart TD
    C["CONTACT = the person<br/>permanent record<br/>name, email, phone, source<br/>Lifecycle Stage: Lead to Customer"]
    D1["DEAL = one opportunity<br/>top-of-funnel stages in HubSpot<br/>+ a coarse outcome from Pulse"]
    D2["DEAL = a new opportunity<br/>cross-sell / re-mortgage, later"]
    C2["Co-applicant CONTACT<br/>joint mortgage"]

    C -->|"one person, many deals over time"| D1
    C -->|"a later, separate opportunity"| D2
    C2 -->|"a deal can have many contacts"| D1
```

---

## 2. The lead journey (what gets created, and when)

The **Lifecycle Stage** is a label on the Contact that advances over time. A **Deal** is
created when there's a real opportunity. HubSpot covers the **top-of-funnel** stages; once
the user wants to proceed, the application runs in **PRYPCO Pulse**, and only a coarse
outcome flows back to the HubSpot deal.

```mermaid
flowchart TD
    L1["Lead lands (Bayut / Dubizzle / organic)"] --> C1["Create / Update CONTACT<br/>Lifecycle Stage = Lead"]
    C1 --> Q{"Qualified?"}
    Q -->|"No - keep nurturing"| C1
    Q -->|"Yes"| D["Create DEAL (top-of-funnel)<br/>New to Contacted to Qualified to AIP to Proceeding"]
    D --> H{"Wants to proceed?"}
    H -->|"No - retarget / nurture"| C1
    H -->|"Yes"| PULSE["Hand off to PRYPCO Pulse<br/>Documents to Underwriting to Valuation to Offer to Completed"]
    PULSE --> OUT["Coarse outcome back to HubSpot deal<br/>Won / Lost; Contact = Customer"]
```

---

## 3. System architecture (overview)

Leads flow in from the portal; the backend owns a local copy and pushes to HubSpot
asynchronously. HubSpot pushes changes back via webhooks, with a scheduled
reconciliation job as a safety net for anything webhooks miss.

```mermaid
flowchart LR
    subgraph SRC["Lead sources"]
        BAYUT["Bayut / Dubizzle"]
    end

    subgraph PORTAL["Shared backend"]
        API["Portal API"]
        DB[("Portal DB<br/>leads + stored HubSpot ids")]
        OUT[("Outbox table")]
        WORKER["Sync Worker<br/>BackgroundService"]
        WH["Webhook Receiver"]
        RECON["Reconciliation Job<br/>scheduled poll"]
    end

    HS["HubSpot CRM<br/>Contacts + Deals (one Mortgage pipeline)"]

    BAYUT --> API
    API --> DB
    API --> OUT
    OUT --> WORKER
    WORKER -->|"REST API: create / update + dedup"| HS
    HS -->|"Webhooks: created / propertyChange / deleted"| WH
    WH --> DB
    RECON <-->|"changed-since poll (safety net)"| HS
    RECON --> DB
```

---

## 4. Outbound sync — portal to HubSpot (with de-duplication)

The web request never calls HubSpot directly: it writes the lead and an outbox row
in one transaction and returns. A worker drains the outbox, resolves the **person**
and then the **inquiry**, links them, and stores the returned ids.

```mermaid
sequenceDiagram
    participant U as Lead (portal form)
    participant API as Portal API
    participant DB as Portal DB + Outbox
    participant W as Sync Worker
    participant HS as HubSpot API

    U->>API: Submit / update lead
    API->>DB: Save lead + outbox row (one transaction)
    API-->>U: 200 OK (no HubSpot call inline)

    W->>DB: Pick up outbox row

    Note over W,HS: Resolve the PERSON
    W->>HS: Search contact (customer id, then email, then phone)
    alt existing person found
        HS-->>W: existing contact id
        W->>HS: Update contact
    else new person
        W->>HS: Create contact
        HS-->>W: new contact id
    end

    Note over W,HS: Resolve the INQUIRY
    W->>HS: Find deal by opportunity_id (stored id first, else search)
    alt deal exists
        W->>HS: Update deal
    else no deal, but contact has an open deal
        W->>HS: Reuse open deal (via v4 associations)
    else genuinely new
        W->>HS: Create deal
    end

    W->>HS: Associate deal to contact (idempotent)
    Note over W,HS: On 429, back off and retry
    W->>DB: Store HubSpot ids, mark outbox row done
```

---

## 5. Inbound + two-way sync — HubSpot back to the portal

Webhooks are HubSpot's version of Salesforce CDC. Validate the signature,
acknowledge fast, then process asynchronously — guarding against echo loops and
field conflicts. A reconciliation poll catches changes webhooks don't emit.

```mermaid
sequenceDiagram
    participant HS as HubSpot
    participant WH as Webhook Receiver
    participant Q as Queue
    participant P as Event Processor
    participant DB as Portal DB
    participant R as Reconciliation Job

    HS->>WH: POST webhook (contact / deal changed)
    WH->>WH: Verify signature
    WH->>Q: Enqueue event
    WH-->>HS: 200 (ack fast)

    Q->>P: Deliver event
    P->>P: Is this our own echo? (compare version / hash)
    alt echo from our own write
        P->>P: Drop it (avoids update loop)
    else genuine HubSpot change
        P->>DB: Check updatedAt + source-of-truth rule
        alt HubSpot newer / owns the field
            P->>DB: Apply update (idempotent)
        else local is newer
            P->>DB: Keep local value
        end
    end

    loop every few minutes
        R->>HS: Fetch records changed since last run
        R->>DB: Reconcile anything webhooks missed
    end
```

---

## 6. Three systems, not one — and who owns what

HubSpot handles top-of-funnel nurturing; once a user wants to proceed, processing happens
in **PRYPCO Pulse**. The backend DB is the hub linking the customer portal, HubSpot, and
Pulse. Each owns one slice of the data; every field flows one way from its owner.

```mermaid
flowchart TD
    CP["Customer Portal (B2C)<br/>leads in (Bayut / Dubizzle / organic)"]
    BE["Backend + DB<br/>linking hub: opportunity_id + stored ids<br/>outbox, webhooks, reconciliation"]
    HS["HubSpot CRM (Pro)<br/>top-of-funnel: nurturing, qualification, retargeting"]
    PULSE["PRYPCO Pulse<br/>application, docs, underwriting, offers, products"]

    CP -->|"new / updated customer data"| BE
    BE -->|"create / update Contact + Deal"| HS
    HS -->|"funnel / engagement changes"| BE
    BE -->|"hand off at 'wants to proceed'"| PULSE
    PULSE -->|"application progress"| BE
    BE -->|"coarse outcome only"| HS
```

Ownership rule of thumb:
- **Customer portal** masters customer-submitted identity + inquiry data.
- **HubSpot** masters top-of-funnel: lifecycle stage, lead status, top-of-funnel deal stage, owner.
- **PRYPCO Pulse** masters the application, documents, offers, and products. HubSpot gets a coarse outcome only.

---

## 7. Top-of-funnel to handoff to Pulse (who owns each stage)

The lead flows from the customer, to HubSpot for top-of-funnel nurturing/qualification, to
**PRYPCO Pulse** for the application — with only a coarse outcome flowing back to HubSpot.

```mermaid
flowchart TD
    subgraph S1["Customer Portal owns this"]
        A["Customer submits lead"]
    end
    subgraph S2["HubSpot owns this - top of funnel"]
        B["Contact lifecycle = Lead"] --> C{"Qualified?"}
        C -->|"keep nurturing / retarget"| B
        C -->|"wants to proceed"| D["Deal stage = Proceeding"]
        OUT["Coarse outcome: Won / Lost"]
    end
    subgraph S3["PRYPCO Pulse owns this"]
        E["Application created"] --> F["Document collection"]
        F --> G["Underwriting / valuation / offer"]
    end

    A -->|"synced to HubSpot"| B
    D -->|"handoff to Pulse"| E
    G -->|"coarse outcome back"| OUT
```

---

### Notes

- Diagrams 1–2 are the **concepts**, 3–5 the **build**, 6–7 the **multi-system picture**.
- The outbound flow (4) is what our PoC app exercises, incl. open-deal reuse via associations.
- The inbound flow (5) is built (lighter — funnel/engagement, not application detail): the
  webhook processor + reconciliation sweep update the local mirror for HubSpot-owned fields.
- Per the 2026-06-04 call: HubSpot is **top-of-funnel only**; **PRYPCO Pulse** is the
  processing system of record. No Offer/Product objects in HubSpot; the offer is a
  human-readable attribute. The HubSpot deal pipeline is top-of-funnel + a coarse outcome.
