# Sig2Email

Central email signatures, legal disclaimers, auto-replies and one-click customer-satisfaction surveys for Microsoft 365.
Implements the requirements in [docs/srs.md](docs/srs.md), packaged for use inside one company: people sign in to the portal
with a user name and password, and every setting – including the Microsoft Entra app registration – is entered in the portal.
There is no `.env` file and nothing to configure before the first start.

- **Server-side mode** – Exchange Online routes outgoing mail through a stateless SMTP service that stamps the signature and hands the message straight back. Works on every device.
- **Client-side mode** – an Outlook add-in inserts the signature while the user writes and updates it when recipients change.
- **Combo mode** – both, with a header so no message is ever signed twice.

## What is in the repository

| Path | What it is |
| --- | --- |
| `src/Sig2Email.Core` | Domain model, template language, rule engine, HTML → plain text/RTF, MIME stamping. No I/O. |
| `src/Sig2Email.Data` | EF Core store (PostgreSQL in production, SQLite for development), tenant isolation, cache (Redis or in-process), blob storage (Azure Blob or files), signature composition shared by all entry points. |
| `src/Sig2Email.Api` | Control-plane REST API (sign-in, accounts, system settings), public survey / tracked-link / image endpoints. Serves the built portal and the Outlook add-in. Swagger UI at `/swagger` in development. |
| `src/Sig2Email.MailProcessor` | The SMTP data plane: ESMTP receiver, pipeline, relay back to Exchange Online. |
| `src/Sig2Email.Worker` | Microsoft Graph directory delta sync and the auto-reply scheduler. |
| `web/portal` | Admin portal (React, TypeScript, Fluent UI, Monaco). |
| `web/addin` | Outlook add-in (event-based activation + task pane) and its manifest template. |
| `tests/Sig2Email.Tests` | Unit and integration tests (in-process SMTP round trips, API security, scripted Graph). |
| `tools/Sig2Email.LoadTest` | Throughput/latency test for the SMTP pipeline. |
| `tools/gen-icons.mjs` | Generates the built-in social icon pack (PNG). |
| `deploy/Dockerfile`, `docker-compose.yml` | Container build and a single-region deployment. |

## Run it locally

Requires the .NET 8 SDK and Node.js 20+. No database, Redis or Microsoft 365 tenant is needed: development uses SQLite, an
in-process cache and a seeded demo organisation.

```bash
# 1. API (creates .data/, seeds the demo tenant) – http://localhost:5080
dotnet run --project src/Sig2Email.Api

# 2. Portal – http://localhost:5173
cd web/portal && npm install && npm run dev
```

Open http://localhost:5173 and sign in with one of the demo accounts shown on the sign-in page: `owner`, `rules`, `designer`,
`analyst` or `manager`, all with the password `sig2email-demo`. Demo data and these accounts exist only in the Development
environment; the service refuses to seed them anywhere else.

To try the mail pipeline:

```bash
# 3. SMTP processor – listens on 2525, relays to localhost:2526
dotnet run --project src/Sig2Email.MailProcessor

# 4. Push mail through it (starts its own sink on 2526 standing in for Exchange Online)
dotnet run -c Release --project tools/Sig2Email.LoadTest -- --count 2000 --concurrency 16 --size-kb 20
```

Any SMTP client can also send to `localhost:2525` from a `@contoso.example` address (for example `sam.carter@contoso.example`).
The demo hands stamped mail to `localhost:2526`; run a tool such as smtp4dev there to read the result.

### Tests

```bash
dotnet test                       # backend: 68 tests
cd web/portal && npm test         # designer compiler and tree operations
cd web/addin && npm install && npm run build   # type-checks and bundles the add-in (the API serves web/addin/dist)
```

## How it works

### Mail flow (server-side)

1. A transport rule in the customer's Exchange Online sends outgoing mail to the regional smart host over TLS.
2. The processor accepts the message only on an encrypted session that presents Microsoft's client certificate
   (`mail.protection.outlook.com`) and identifies the tenant from the `X-MS-Exchange-CrossTenant-Id` header. Unknown
   organisations get `550` — the service never relays for strangers.
3. Sender attributes and the compiled rule set come from cache. Rules are evaluated in order; each match renders its template.
4. Signatures are inserted above the quoted thread (or at the bottom for disclaimers) in the HTML and plain-text parts. Images
   are linked or embedded as inline parts, per template.
5. `X-Sig2Email-Processed: true` is added (the transport rule has an exception on it, which prevents loops) and the message
   goes back to the tenant's Exchange endpoint, authenticated by our certificate.

Properties worth knowing:

- **Fail open.** If stamping throws, the original bytes are relayed untouched. If the hand-back fails the sender gets `451`,
  so Exchange keeps the message queued and retries. Signed, encrypted and auto-generated messages are never modified.
- **Zero persistence.** Message content exists only in process memory and is never logged. The only things stored about a
  message are survey-dispatch metadata (sender, recipients, subject, message id) and daily counters.
- **Stateless.** Any number of processor instances can run behind a TCP load balancer.

### Template language

```html
<b>{DisplayName}</b>{if JobTitle} · {JobTitle}{/if}
{if MobilePhone}M: {MobilePhone}{/if}
{if Department == "Sales" and Country != "Germany"}…{else}…{/if}
<img src="{Photo}">  <img src="{QrCode}">  {Csat:<survey id>}
```

Placeholders: `FirstName LastName DisplayName Email JobTitle Department Company Office Phone MobilePhone Fax Street City
State PostalCode Country CustomAttribute1…15 Photo QrCode CurrentYear CurrentDate`. Values are HTML-encoded; unknown `{…}`
is left alone so CSS survives. The visual designer compiles its blocks to this language.

Templates have a **draft** and a **published version**. Mail flow only ever uses the published version, which is how
"designers cannot alter live mail flow" is enforced: designers edit drafts, rule administrators and owners publish.

### Sign-in and roles

People sign in to the portal with a **user name and password**. Accounts are created by an owner under **Accounts**; there is
no self-registration and no password-reset email (an owner resets it).

- Passwords are stored as salted PBKDF2-SHA256 hashes (600,000 iterations), at least 10 characters.
- Five wrong passwords lock an account for 15 minutes; sign-in is also rate-limited per client address.
- An account created or reset by an owner gets a temporary password that must be replaced at first sign-in.
- Changing or resetting a password, or disabling an account, ends all of its sessions at once. Sessions last 12 hours and live
  in the browser tab only.
- Account changes, lock-outs and settings changes are written to the audit log.

| | Owner | Rule admin | Designer | Analytics viewer |
| --- | :-: | :-: | :-: | :-: |
| Accounts, system settings, mail-flow script | ✓ | | | |
| Rules, surveys, publish templates, auto-replies | ✓ | ✓ | | |
| Edit templates and images | ✓ | | ✓ | |
| View rules, templates, directory | ✓ | ✓ | ✓ | |
| Analytics | ✓ | ✓ | | ✓ |
| Audit log | ✓ | | | ✓ |

An account can also have no administrative role and be linked to a person in the directory ("Manager"): it can only schedule
auto-replies for that person's direct reports.

The **Outlook add-in** is the one place that still uses Microsoft sign-in: it has to know whose mailbox it is running in, so it
presents the user's Microsoft token (for the Entra app entered under System). Those tokens identify a mailbox user only and
never carry a portal role.

### Where settings live

| What | Where |
| --- | --- |
| Public address, Entra directory/client ID and secret, smart-host name, TLS certificate, TLS and client-certificate policy, relay port, alert mail server, sync intervals | Portal → **System**. Stored encrypted (AES-GCM) in the database; all three services pick up a change within a few seconds, no restart. |
| Organisation name, deployment mode, Exchange endpoint for returning mail | Portal → **Organisation** |
| Portal accounts and roles | Portal → **Accounts** |
| Database/cache location, data directory, listening ports | Start-up wiring in `docker-compose.yml` / `appsettings.json`. These are not secrets and have working defaults. |

The data directory holds `installation.key`, generated on first start. It signs survey and tracking links and encrypts the
stored settings. **Back it up with the database**: a database restored without it loses the stored secrets (they have to be
entered again) and links in emails already sent stop working.

## Production deployment

### 1. Start the services

```bash
docker compose up --build -d
docker compose logs api      # prints the one-time setup code
```

This starts PostgreSQL, Redis, the API with the portal, the SMTP processor and the worker. Nothing has to be filled in first.
Also needed around it:

- A TLS-terminating reverse proxy in front of the API (port 8080), reachable by email recipients (survey pages, images).
- Port 25 reachable from Exchange Online, with a public DNS name for it.

### 2. Create the first owner

Open the portal. On a new installation it asks for the **setup code** (from the service log, or `setup-code.txt` in the data
directory), the organisation name and the first owner's user name and password. The code exists so that whoever reaches the
URL first cannot claim the installation; it is deleted once used.

### 3. Configure it in the portal (System)

1. **Public address** – the https address of the reverse proxy.
2. **Microsoft Entra app registration** – in your Entra admin center register an application (single tenant):
   - **API permissions → Microsoft Graph → Application**: `User.Read.All`, `Group.Read.All`, `Organization.Read.All`,
     `MailboxSettings.ReadWrite`, then **Grant admin consent**. (`Organization.Read.All` is one more than the SRS lists;
     without it accepted domains are derived from mailbox addresses.)
   - Create a client secret.
   - Enter the directory (tenant) ID, application (client) ID and secret under System, save, and press **Test connection**.
     When it passes, the first directory sync starts.
3. **Mail routing** (server-side or combo mode) – the public name of the mail receiver and a **TLS certificate** for that name
   from a public CA, uploaded as a `.pfx` file. Exchange validates it when sending to you and when you send back.
4. **Organisation → Exchange Online mail flow → Download setup script**, and run it as an Exchange Administrator. It creates the
   two connectors and the transport rule (optionally scoped to one group). Enter your Exchange endpoint
   (`<domain>.mail.protection.outlook.com`) on the same page.
5. **Outlook add-in** (client-side or combo mode) – in the same app registration, expose an API (`api://<client id>`, scope
   `access_as_user`) and add the single-page-application redirect URI shown under System. Then **Download manifest** and upload
   it in Microsoft 365 admin center → Integrated apps. The add-in is served by the API itself; it is not built per installation.
6. **Alert emails** (optional) – the mail server for negative-rating notifications, with a test button.

To add a database migration during development: `dotnet ef migrations add <Name> --project src/Sig2Email.Data`.

## Measured on a developer laptop

Everything (load generator, processor, sink, SQLite) on one Windows machine, 32 connections, ~20 KB messages, every message
stamped: **≈17,500 messages/minute, p50 101 ms, p95 175 ms, p99 212 ms** once warm (the first few messages after a cold
start take about 2 s). 25 MB messages are processed correctly but take **≈2 s at the median** on the same machine, which is
above the SRS target of 500 ms; see below.

## Known gaps and things not verified

Read this before relying on the system.

- **Nothing has been run against a real Microsoft 365 tenant.** Graph sync and auto-replies are tested against a scripted
  stand-in; the connection test, the add-in's Microsoft sign-in, the generated Exchange PowerShell and the Outlook add-in
  itself (built and type-checked only) all need a first run in a test tenant.
- **Sign-in is user name and password only.** There is no second factor and no single sign-on for the portal. Put the portal
  behind your VPN or an IP allow-list if it should not be reachable from the internet; the survey, link and image endpoints
  (`/r`, `/l`, `/assets`) are the only parts email recipients need.
- **The database password in `docker-compose.yml` is fixed** (`sig2email-internal`). PostgreSQL is not published outside the
  compose network; change it in that file if your policy requires a unique one.
- **The SMTP listening port and connection limits** are start-up settings, not portal settings (changing a port means
  re-publishing the container port anyway).
- **PostgreSQL, Redis, Azure Blob and the Docker files are untested here** (no Docker daemon was available). All automated
  tests and manual runs used SQLite, the in-process cache and file blobs. The migration was generated but never applied.
- **Large-message latency.** The < 500 ms p95 target holds for ordinary mail, not for 25 MB messages.
- **Fail-open when the service is unreachable** relies on Exchange: mail queues and retries rather than bypassing. The
  transport rule can be disabled to bypass at once; automatic disabling by a watchdog is not built. List several smart
  hosts in the connector for regional redundancy.
- **Analytics aggregation** happens in memory over the selected date range — fine for thousands of ratings per period, not
  for millions.
- **Profile photos** are refreshed when a user object changes, not when only the photo changes.
- **RTF** output is a basic conversion (text, emphasis, links). TNEF-only (winmail.dat) messages pass through unstamped.
- **DKIM**: the processor does not sign. Exchange Online signs on final delivery, after the message has come back.
- **Dark-mode preview** in the designer approximates Outlook; mail clients differ.
- The built-in social icons are simple generated glyphs, not the networks' official logos. Upload your own images for those.
- SOC 2 / ISO 27001 are organisational certifications; the code provides the audit log, tenant isolation and
  zero-persistence design they would examine, nothing more. No penetration test has been performed.
