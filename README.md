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
