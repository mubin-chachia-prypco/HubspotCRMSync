# HubSpot App & Authentication Setup (Sandbox → Production)

How the integration authenticates to HubSpot, how to create the app, and how to repeat
it for production. Written after a painful round of HubSpot's shifting UI — the point of
this doc is that nobody has to rediscover it.

## TL;DR

- The service authenticates with a **Private App token** (also called the "service key")
  — a permanent bearer token scoped to this integration only.
- Two secrets feed the service: **`HUBSPOT_TOKEN`** (API calls) and
  **`HUBSPOT_CLIENT_SECRET`** (webhook signature validation). Base URL is `https://api.hubapi.com`.
- Scopes are managed directly in the Private App UI — no CLI deploy needed to add/change them.
- Run the same steps once per environment — **sandbox** and **production** — each yields
  its own token.

## Why Private App token (and what we tried before)

We started on the HubSpot Projects platform (static-auth projects app) but moved to a
**Private App** ("service key") for simplicity:

| | Projects platform | Private App (current) |
|---|---|---|
| Scope changes | Edit hsmeta, `hs project upload`, reinstall, rotate token | Edit in UI, rotate token |
| Webhook subscriptions | Config-as-code (`hs project upload`) | UI or API |
| Token type | Static bearer, non-expiring | Static bearer, non-expiring |
| Migration risk | HubSpot phasing out legacy; projects path is current | Labelled "Legacy" but still fully supported and the simpler path for single-account integrations |

For a single-account, server-to-server integration the Private App token is the right
call — it's one credential, managed in one place, with no CLI toolchain required to
update scopes.

> **Note:** We still keep the HubSpot project (`HubspotApps/TestCRMSync`) for webhook
> subscription config. The project no longer issues the token — its only role now is
> deploying the webhook subscription definitions via `hs project upload`.

## How the pieces fit together

```
Private App (HubSpot UI) ──► service key (HUBSPOT_TOKEN)
                                        │
                                        ▼
        .NET service ── Authorization: Bearer <token> ──► api.hubapi.com/crm/v3/...

HubSpot Project (hsmeta) ── hs project upload ──► webhook subscriptions registered
                                        │
                                        ▼
                              HubSpot ──► POST /webhooks/hubspot (signed with HUBSPOT_CLIENT_SECRET)
```

## Creating the Private App (run once per environment)

1. In HubSpot: **Settings → Integrations → Private Apps → Create a private app**.
2. Give it a name (e.g. `SyncApp Service Key`) and description.
3. On the **Scopes** tab, add:
   - `crm.objects.contacts.read` + `write`
   - `crm.objects.deals.read` + `write`
   - `crm.objects.custom.read` + `write`
   - `crm.schemas.custom.read`
4. Click **Create app** → confirm.
5. Copy the token shown (starts `pat-eu1-…`). This is `HUBSPOT_TOKEN`. **It is only shown once in full** — copy it now.
6. The **Client secret** for webhook signature validation is on the same page. Copy it too — this is `HUBSPOT_CLIENT_SECRET`.

## Rotating the token

If the token is compromised or you want to cycle it:

1. **Settings → Integrations → Private Apps → your app → Rotate token**.
2. Update `appsettings.json` (local dev) or the secret store (production) with the new value.
3. Restart the service. No HubSpot project upload or reinstall needed.

## Adding or changing scopes

1. **Settings → Integrations → Private Apps → your app → Scopes tab**.
2. Add the new scope → **Update app**.
3. Rotate the token — the new token carries the updated scope grant.
4. Update `appsettings.json` / secret store with the rotated token.

No `hs project upload` needed for scope changes.

## Wiring the service

```bash
export HUBSPOT_TOKEN=<private app token>
export HUBSPOT_CLIENT_SECRET=<client secret>
export HUBSPOT_BASE_URL=https://api.hubapi.com   # same host for sandbox and prod
```

In production these belong in a **secret store / CI secrets**, never in source or
`appsettings.json` (which holds placeholders only). Each environment uses its own token.

> ⚠️ `appsettings.json` is in `.gitignore` and must never be committed — it contains the
> live token and client secret.

## Webhooks

Webhook subscriptions are still managed via the HubSpot project config-as-code:
they live in `HubspotApps/TestCRMSync/src/app/webhooks/webhooks-hsmeta.json` and deploy
with `hs project upload` + reinstall.

- Point the target at the service's public HTTPS endpoint: `…/webhooks/hubspot`
  (a tunnel such as ngrok/cloudflared in dev; the real host in prod).
- The webhook client secret comes from the **Private App**, not the project — use the same
  `HUBSPOT_CLIENT_SECRET` from step 6 above.
- Requests are signed with the **v3** signature; the service validates this automatically.
  HubSpot expects an ack within ~5 seconds.

See `hubspot-config-and-operations.md` §4 for the full subscription change process.

## Sandbox vs production

Same steps, different account — each gets its own Private App and its own token.

| Environment | Account | Portal ID |
|---|---|---|
| Sandbox | PRYPCO — B2C CRM [SANDBOX] (`c8e055.prypco.com`) | `148631333` |
| Production | PRYPCO (`prypco.com`) | `148068623` |

For production: create a new Private App in the prod portal with the same scopes, copy
its token + client secret into the prod secret store. Webhook target URL changes to the
prod host; update `webhooks-hsmeta.json` and run `hs project upload` pointed at prod.

## Account-side prerequisites (both environments)

Create these before the first sync, or create/update calls will reject unknown properties:
- **Contact:** `portal_customer_id` (unique if the tier allows), `lead_source`.
- **Deal:** `opportunity_id` (unique), `partner_lead_ref`, `lead_source`, `customer_profile_snapshot`, `dropped_at`, `offers_seen_snapshot`.
- Make the **Mortgage** pipeline the **default** deal pipeline (or pass `pipeline` explicitly).
- **Custom object:** create the `Application` object, define its association to Deal, note the
  type ID (e.g. `2-203884532`) and set `ApplicationObjectTypeId` in config.

(See `hubspot-config-and-operations.md` §5–6 for the full property list and custom object setup.)

## Gotchas (things that cost us time)

- **Scope changes need a token rotation.** After updating scopes in the Private App UI, the
  existing token still carries the old scope grant. Rotate the token to get a new one that
  includes the updated scopes.
- **`hs project upload` blocks if subscriptions are removed.** Never delete an entry from
  `webhooks-hsmeta.json` — set `"active": false` instead. See `hubspot-config-and-operations.md` §4.
- **`crmObjects` and `legacyCrmObjects` must coexist.** Previously-deployed legacy subscriptions
  (e.g. `contact.privacyDeletion`) must stay in the `legacyCrmObjects` block as `active: false`
  or the upload will fail with a component-removal error.
- **`privacy.deletion` is not a valid `crmObjects` subscription type.** It only works in
  `legacyCrmObjects`. Valid `crmObjects` types: `object.creation`, `object.deletion`,
  `object.merge`, `object.restore`, `object.propertyChange`, `object.associationChange`.
- **The CLI personal access key is not the service token.** `hs init` stores a personal key
  in `hubspot.config.yml` for the CLI only. The service token is the Private App token.
- **`appsettings.json` must never be committed.** It holds the live token and client secret.
  It is in `.gitignore`. Check before every push.