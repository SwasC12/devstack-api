# DevStack API

A catalogue of every tool, service and subscription used across my projects
(Supabase, Cloudinary, MonsterASP, Resend, Vercel, …). C# backend; Angular
frontend (`devstack-ui`) to follow.

## Architecture (layered, mirrors TDC convention)

| Project | Responsibility |
|---|---|
| `DevStack.API.Models` | Entities / DTOs (`Tool`) |
| `DevStack.API.DataAccess` | EF Core (SQL Server), `DevStackDataModel`, repositories |
| `DevStack.API.PlatformLogic` | Business logic / rules |
| `DevStack.API.WebService` | Controllers, `Program.cs`, Swagger — the runnable app |

Reference direction: `WebService → PlatformLogic → DataAccess → Models`.

## Run locally

```bash
cd DevStack.API.WebService
dotnet run
```

Then open **`/swagger`**. Uses SQL Server LocalDB (`(localdb)\MSSQLLocalDB`,
database `DevStack`); EF migrations are applied automatically on startup and a
few tools are seeded on first run.

## Database migrations

```bash
dotnet ef migrations add <Name> --project DevStack.API.DataAccess --startup-project DevStack.API.WebService
```

## Deployment

Hosted on **MonsterASP.NET**. Pushing to `main` triggers the GitHub Actions
workflow in `.github/workflows/deploy-monsterasp.yml`, which publishes the
`WebService` project and deploys it via Web Deploy. The production SQL
connection string is injected from a GitHub secret at deploy time (never
committed).

### GitHub secrets used at deploy time

| Secret | Needed for |
|---|---|
| `MONSTER_DB_PASSWORD` | Production SQL connection string (required) |
| `MONSTER_JWT_KEY` | Signing key for auth tokens. **Set it** — the committed value is a dev-only key. Any long random string ≥ 32 chars. |
| `CLOUDINARY_API_KEY` / `CLOUDINARY_API_SECRET` | Server-side image cleanup when a menu item is deleted. Without them, delete works but the Cloudinary image is orphaned. |
| `OPS_KEY` | Guards `POST api/auth/superadmin-reset`, the recovery hatch for a lost superadmin password. Without it that endpoint returns 503. Keep a copy in your password manager. |

## Shop lifecycle (platform owner)

Shops can be **suspended** and **reactivated** by the superadmin
(`PUT api/shops/{id}/status`). A suspended shop is blocked at the door: login,
PIN login, staff lookup and token refresh all refuse it, so existing sessions
die within the 15-minute access-token lifetime.

`POST api/shops/{id}/reset-admin-password` sets a fresh random password for the
shop's first admin and returns it **once** (there is no email system — the
platform owner relays it). Only the bcrypt hash is stored.

## Superadmin recovery hatch

`POST api/auth/superadmin-reset` resets the **platform superadmin** password
without needing a superadmin login or direct DB access — useful when the
password is lost. It is guarded by the `Ops:Key` config value (env
`Ops__Key`, injected from the `OPS_KEY` GitHub secret at deploy time):

```bash
curl -X POST https://devstack-api.runasp.net/api/auth/superadmin-reset \
  -H "X-Ops-Key: <ops key>"
```

Returns the new one-time password **once** (only the bcrypt hash is stored)
and burns the superadmin's refresh-token chain so existing sessions die. If no
ops key is configured the endpoint returns 503 — it fails closed.

## Order integrity & history

- Prices and names on an order come from the **database**, never the client.
- Stock decrements are **atomic** (`UPDATE … WHERE StockQuantity >= qty` inside
a transaction), so two concurrent checkouts can't oversell.
- Every order records its **cashier** (`UserId`).
- Admin can **void** an order (`POST api/orders/{id}/void`, reason required);
voided orders are excluded from revenue and their stock is restored.
