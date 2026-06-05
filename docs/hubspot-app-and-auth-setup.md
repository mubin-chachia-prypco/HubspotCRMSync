# HubSpot App & Authentication Setup (Sandbox → Production)

How the integration authenticates to HubSpot, how to create the app, and how to repeat
it for production. Written after a painful round of HubSpot's shifting UI — the point of
this doc is that nobody has to rediscover it.

## TL;DR

- The service authenticates with a **static access token** from a **developer-platform
  "projects" app** — *not* a legacy private app, and *not* OAuth.
- Two secrets feed the service: **`HUBSPOT_TOKEN`** (API calls) and
  **`HUBSPOT_CLIENT_SECRET`** (webhook signature). Base URL is `https://api.hubapi.com`.
- The HubSpot project (named **SyncApp**) is config-as-code that *mints the token*. It is
  separate from the .NET service; the only thing connecting them is the token.
- Run the same steps once per environment — **sandbox** and **production** — each yields
  its own token.

## Why this approach (and what we deliberately avoided)

HubSpot has three account types, and only some can issue tokens:
- **App developer account** — builds public/marketplace apps. **Cannot** create the
  single-account credential we need. (This is the account that keeps pushing the CLI /
  projects onboarding and shows "no legacy apps.")
- **Developer test account** and **standard account (Free–Enterprise)** — *can* issue
  single-account tokens. Our sandbox and production are standard accounts.

Within those, there were three possible credentials:
1. **Legacy private app** — works today, but HubSpot has renamed these "Legacy apps" and
   flagged them for forced migration to the projects platform. Building a system of record
   on it is a ticking clock, so we don't.
2. **Projects app + OAuth** — fully future-proof and multi-account, but requires a
   token-exchange + refresh-token flow in our backend. Unnecessary for a single-account,
   server-to-server integration, and more code to maintain.
3. **Projects app + static auth** ← **our choice.** It's on the current platform (no
   migration cliff) *and* issues a permanent, non-expiring bearer token — so the .NET
   service uses it exactly like a classic token, with **zero auth code**.

If we ever need to install across many portals or list on the marketplace, that's the
moment to switch to OAuth (and add the refresh flow). Not before.

## How the pieces fit together

```
SyncApp (HubSpot project) ── hs project upload ──► app registered in the HubSpot account
                                                        │ issues
                                                        ▼
                                          static access token + client secret
                                                        │ copied into env vars / secret store
                                                        ▼
        .NET service ──── Authorization: Bearer <token> ────► api.hubapi.com/crm/v3/...
```

The `.NET` code imports nothing from the project and does not run inside HubSpot. The
project is the app's "paperwork"; the service is the integration.

## Creating the app (run once per environment)

**Prerequisites**
- Node.js + the HubSpot CLI: `npm install -g @hubspot/cli@latest`.
- CLI authenticated to the **target** account: `hs init` (first time) or `hs account auth`
  (add another account). This uses a **personal access key**.
- ⚠️ The personal access key authenticates the **CLI only**. It is **not** the app token,
  and it must **never** be committed (it lands in `hubspot.config.yml` — gitignore it).

**Steps**
1. Scaffold the project:
   ```bash
   hs project create --name SyncApp
   ```
   Answer the prompts: **App** → **Privately** → **Static Auth** → add the **Webhook**
   feature only (skip Card / Functions / Settings / Pages / Workflow Action / SCIM).
2. Fix the generated `src/app/app-hsmeta.json` — the scaffold ships two things to correct:
   - It seeds a stray **`"oauth"`** entry in `requiredScopes`. **Remove it** — it isn't a
     real scope for static auth, and it'll show up as a phantom 5th scope after install.
   - The `uid`, `name`, and `description` carry placeholder text (e.g. "TestCRMSync"). Rename
     them to the real app so prod isn't carrying a test label.
   The corrected config:
   ```jsonc
   {
     "uid": "syncapp",
     "type": "app",
     "config": {
       "name": "SyncApp",
       "description": "Syncs B2C mortgage leads between the portal and HubSpot (top-of-funnel).",
       "distribution": "private",
       "auth": {
         "type": "static",
         "requiredScopes": [
           "crm.objects.contacts.read",
           "crm.objects.contacts.write",
           "crm.objects.deals.read",
           "crm.objects.deals.write"
         ],
         "optionalScopes": [],
         "conditionallyRequiredScopes": []
       },
       "permittedUrls": { "fetch": ["https://api.hubapi.com"], "iframe": [], "img": [] }
     }
   }
   ```
   (Scopes can be expanded later without recreating the app. If you already uploaded with the
   stray `oauth` scope, just fix it and re-upload — the reinstall reflects the clean list.)
3. Upload/deploy to the connected account:
   ```bash
   hs project upload
   ```
4. **Install the app — this is the step that issues the token.** In HubSpot:
   **Development → Projects → SyncApp → Distribution tab**. Under *Manage distribution*, click
   **Install now**, review the requested scopes, then click **Connect app**. (A static-token
   app installs in **one standard account at a time**, plus up to 10 developer test accounts.)
5. Grab the two credentials — they live in **two different places**:
   - **Access token** → on the **Distribution tab, and only after a successful install**:
     click **Show**, then **Copy**. This is `HUBSPOT_TOKEN`. It does **not** appear anywhere
     before the install completes.
   - **Client secret** → on the **Auth tab** ("Used for webhook signature validation"):
     **Show** → **Copy**. This is `HUBSPOT_CLIENT_SECRET`.
   - The **Client ID** on the Auth tab is an OAuth artifact — **not needed** for static auth.
   - Viewing or rotating the access token requires **super admin or a developer seat** on that
     account. If **Show** is greyed out or missing, that permission is why.

## Wiring the service

```bash
export HUBSPOT_TOKEN=<access token>
export HUBSPOT_CLIENT_SECRET=<client secret>
export HUBSPOT_BASE_URL=https://api.hubapi.com   # same host for sandbox and prod
```

In production these belong in a **secret store / CI secrets**, never in source or
`appsettings.json` (which holds placeholders only). Each environment uses its own token.

## Webhooks

On this platform, webhook subscriptions are **config in the project**, not clicks in a UI:
they live in `src/app/webhooks/*-hsmeta.json` and deploy with `hs project upload`.
- Point the target at the service's public HTTPS endpoint: `…/webhooks/hubspot`
  (a tunnel such as ngrok/cloudflared in dev; the real host in prod).
- Subscribe to `contact.creation`, `contact.propertyChange`, `deal.creation`,
  `deal.propertyChange` (at least the qualification fields we act on).
- Requests are signed with the **v3** signature using the app **client secret**; the
  service already validates this. HubSpot expects an ack within ~5 seconds.

## Sandbox vs production

Same steps, different account — each gets its own app install and its own token.

| Environment | Account | Portal ID |
|---|---|---|
| Sandbox | PRYPCO — B2C CRM [SANDBOX] (`c8e055.prypco.com`) | `148631333` |
| Production | PRYPCO (`prypco.com`) | `148068623` |

For production: `hs account auth` to connect the CLI to the prod portal, `hs project upload`
the same project there, install on prod via the Distribution tab, then copy prod's token +
client secret into the prod secret store. Webhook target URL changes to the prod host.

## Account-side prerequisites (both environments)

Create these before the first sync, or create/update calls will reject unknown properties:
- **Contact:** `portal_customer_id` (unique if the tier allows), `lead_source`.
- **Deal:** `opportunity_id` (unique), `partner_lead_ref`, `dropped_at`, `offers_seen_snapshot`.
- Make the **Mortgage** pipeline the **default** deal pipeline (or pass `pipeline` explicitly).

(See `hubspot-integration-plan.md` §10 for the full checklist.)

## Gotchas (things that cost us time)

- **You can't create this in an app developer account.** Developer accounts only build
  public/marketplace apps. Create/install the app in the standard CRM account (sandbox or
  prod). If the UI keeps pushing the CLI/projects onboarding and shows "no legacy apps,"
  you're in a developer account.
- **The personal access key is not the app token.** `hs init` stores a personal access key
  in `hubspot.config.yml` to authenticate the *CLI*. The app's `HUBSPOT_TOKEN` is a different
  credential, produced by installing the app.
- **The access token only exists after install, and lives on the Distribution tab** — not the
  Auth tab. Before install there is only a Client ID + Client secret.
- **The scaffold seeds a stray `oauth` scope.** Remove it before upload (see step 2).
- **"Private app" is now labelled "Legacy app."** That's the deprecated path; we use the
  projects + static-auth path instead.
- **Showing/rotating the token needs super admin or a developer seat** on the account.

## Migration note

Static-auth projects apps are the current, supported platform. Legacy private apps are
being sunset; we chose this path specifically to avoid a forced migration of the auth layer
later. If HubSpot changes the static-auth mechanics, only this doc and the two env values
should need revisiting — the service code is unaffected.