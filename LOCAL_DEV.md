# Running the producer locally (HubspotCRMSync)

The CRM-agnostic **.NET producer**: `/ingest` envelope ingress, partner lead intake (`/intake/{source}`,
spec §15), transactional outbox, and (in non-local envs) the Service Bus consumer that calls the
adapter. **No HubSpot code lives here.** Full design: [`docs/forwarder-adapter-spec.md`](docs/forwarder-adapter-spec.md).

## Prerequisites
- **.NET 8 SDK** (`dotnet --version` → 8.x).
- **PostgreSQL** running locally (default conn points at `localhost:5432`, db `hubspot_sync`).
- **GitHub Packages PAT** with `read:packages`, **SSO-authorised for the `Prypco` org** — needed to
  restore `Prypto.ServiceBusHelpers` from the private feed in [`nuget.config`](nuget.config).
- `dotnet-ef` tool: `dotnet tool install --global dotnet-ef`.
- *(Optional)* an Azure Service Bus namespace — only if you want to exercise the
  producer→queue→consumer→adapter path. Not needed for `/ingest` or `/intake` ingress testing.

## 1. Authenticate NuGet to the private feed
The `github` source exists in `nuget.config` but carries no credentials (never commit a PAT). Add them
to your **user-level** NuGet config:
```bash
dotnet nuget update source github \
  --username <your-github-username> \
  --password <your-PAT> \
  --store-password-in-clear-text
# (first time, if the named source isn't in your user config yet, use `add source` with
#  value https://nuget.pkg.github.com/Prypco/index.json)
dotnet restore
```

## 2. Postgres
```bash
createdb hubspot_sync         # or: psql -c 'create database hubspot_sync;'
```
Connection string lives in [`appsettings.json`](appsettings.json) → `ConnectionStrings:HubSpotSyncConnection`
(`Host=localhost;Port=5432;Database=hubspot_sync;Username=postgres;Password=postgres`). Adjust to your
local Postgres creds.

## 3. Create the schema (EF migration)
No migrations exist yet — generate the first one (covers the outbox **and** `inbound_leads`):
```bash
dotnet ef migrations add InitialSchema
dotnet ef database update
```

## 4. Run
```bash
ASPNETCORE_ENVIRONMENT=local dotnet run
```
`local` **disables the Service Bus consumer** (see `ServiceExtensions.RegisterMessageConsumers`), so you
can run ingress + intake without a queue. App listens on `http://localhost:5080` (see `.vscode/launch.json`).
In VS Code: **Run → "Run producer (local)"**.

## Endpoints
| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | liveness |
| POST | `/ingest` | CRM-sync envelope → outbox → (non-local) Service Bus → adapter |
| POST | `/intake/{source}` | partner lead intake (`source` ∈ {dubizzle, bayut}); stores raw payload, returns `{ token }` (UUIDv7) |
| GET | `/intake/redeem/{token}` | redeem token → stored payload (one-time, 5 min TTL; else `410`) |

### Try the intake flow (no HubSpot / queue needed)
```bash
# store a lead → get a token
TOKEN=$(curl -s -X POST localhost:5080/intake/bayut \
  -H 'content-type: application/json' \
  -d '{"name":"Jane","phone":"+9715...","listingId":"BAYUT-123"}' | jq -r .token)

# redeem it (works once, within 5 min) → returns the stored payload verbatim
curl -i localhost:5080/intake/redeem/$TOKEN
# redeem again → 410 Gone
```

## Config reference
| Key | Meaning | Local |
|---|---|---|
| `ConnectionStrings:HubSpotSyncConnection` | Postgres | localhost default |
| `ServiceBusSettings:ConnectionString` | Azure Service Bus | placeholder; only used by the consumer (non-local) |
| `HubSpotSyncSettings:AdapterIngestUrl` | adapter API URL (k8s service, path `/api/ingest`) | placeholder; cloud only |
| `HubSpotSyncSettings:AdapterServiceToken` | service token sent as `X-AI-Agent-Key` to the adapter | empty locally; secret in prod |
| `Intake:{Source}:BearerToken` | per-partner Bearer token for `POST /intake/{source}` (e.g. `Intake:Dubizzle:BearerToken`, `Intake:Bayut:BearerToken`) | **empty = no auth** (set to require `Authorization: Bearer …`) |

## Notes / not-yet
- **Service Bus + the adapter call are cloud-path** — locally the consumer is off. End-to-end
  (producer→queue→Function→HubSpot) needs Azure provisioning (see the adapter's `SETUP.md`).
- Inbound POST **auth is optional/off by default** — a per-partner static **Bearer token**
  (`Authorization: Bearer …`) checked only when `Intake:{Source}:BearerToken` is set (Key Vault in prod).
