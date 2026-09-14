# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Ledgerly is a personal finance web app: bills (amounts, due dates, pay-from account), running-balance projections, income, loans with amortization, per-user accounts with an admin role. User-facing setup (admins, email) is documented in README.md.

## Commands

```powershell
dotnet run --project src/Ledgerly                      # http://localhost:5005 (launchSettings "http" profile)
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~LoanPaymentsTests"                                      # one class
dotnet test --filter "FullyQualifiedName=Ledgerly.Tests.ProjectionTests.Same_day_income_is_applied_before_bills"  # one test
dotnet ef migrations add <Name> --project src/Ledgerly --output-dir Data/Migrations
```

- The running app locks `src/Ledgerly/bin`, so `dotnet build`, `dotnet test` (without `--no-build`) and `dotnet ef` fail while it runs. Stop the process listening on 5005 first.
- Migrations apply automatically on startup (`Program.cs`), followed by `AdminService.EnsureAdminExistsAsync`. When adding a non-nullable enum-as-string column, fix the generated `defaultValue: ""` to a real enum name, or existing rows won't load.
- Point the app at a throwaway database with `ConnectionStrings__Ledgerly="Data Source=<path>"` (default `App_Data/ledgerly.db`, gitignored).
- No linter is configured; the build should stay warning-free.

## Branches, releases and deployment

- `beta` → Beta channel, `main` → Production. Do day-to-day work on `beta` (or feature branches merged into it); promote by merging `beta` into `main`. Pull requests into either run `.github/workflows/ci.yml`.
- Every push to `beta`/`main` runs `.github/workflows/release.yml`: tests, then a multi-arch image to `ghcr.io/007darkmatter5/ledgerly` (tags `beta` + `<ver>-beta`, or `production` + `latest` + `<ver>`) and a GitHub release `v<ver>` (pre-release for beta). Version = `VERSION` (major.minor) + `.<run number>`, passed as `-p:Version`/`VERSION` build arg; `AppInfo` exposes it plus `Ledgerly:Channel`.
- The `Dockerfile` cross-publishes with `$BUILDPLATFORM`/`-a $TARGETARCH` so arm64 builds need no emulation; keep `RUN` out of the final stage for the same reason. `docker/entrypoint.sh` starts as root, chowns `/data` and `/keys` to `PUID:PGID` (default 1654) and drops privileges with `setpriv` (or runs directly if started with `--user`). The database is in `/data`, Data Protection keys in `/keys` (`DataProtection:KeysPath`), and `/healthz` reports health.
- Servers install/update with `deploy/install.sh <beta|production>` (Docker Compose under `/opt/ledgerly/<channel>`; it writes `.env` and `docker-compose.yml`, never overwrites `ledgerly.env`, backs up the DB before restarting, and waits on `/healthz`). The script is fetched from `main`, so changes to it reach both channels once merged. No Docker on the dev PC: verify image changes through the Actions run (`gh run watch`).
- Migrations run on container start and can't be rolled back automatically; a rollback means installing the older `--version` and restoring the pre-update backup.
- In the Dockerfile, restore only after `COPY src/`. A csproj-only restore layer silently drops Blazor's framework assets (`_framework/blazor.web.js`), so pages render but nothing interactive works; the publish step asserts the file exists.
- The release workflow's `Browser test` step runs `tests/e2e/interactivity.mjs` (Playwright) against the built image with Unraid settings: it signs up and checks the theme toggle and account menu respond. Extend it when adding features that only work interactively.
- Unraid: `deploy/unraid/docker-compose.yml` (Compose Manager plugin) is a single service with `PUID=99`/`PGID=100` and `/etc/localtime` mounted. Don't add init/sidecar containers: stopped one-shot services show as unhealthy in Unraid. The release workflow smoke-tests the image three ways (root-owned folders + PUID/PGID, defaults, `--user`) before publishing.
- Culture is fixed in `Program.cs` (`Ledgerly:Culture`, default en-US) via `DefaultThreadCurrentCulture` + request localization, because containers have no `LANG` and would format money with the invariant culture (`¤`).

## Architecture

**Rendering.** Global Interactive Server render mode, except pages marked `[ExcludeFromInteractiveRouting]`: everything under `Components/Account` (Identity needs `HttpContext` for cookies) and `Components/Status`. `App.razor` picks the mode per request via `HttpContext.AcceptsInteractiveRouting()`. Static pages use `AccountLayout`/`ManageLayout` and plain HTML forms styled by `wwwroot/account.css`; MudBlazor inputs don't work there. Interactive pages use `MainLayout` (MudBlazor).

**Authorization.** `Components/Pages/_Imports.razor` puts `[Authorize]` on every app page. Error/NotFound live in `Components/Status` so they carry no authorization metadata (status-code re-execution throws if they do). Admin pages are under `Account/Pages/Manage/Admin`, whose `_Imports` adds `[Authorize(Roles = "Admin")]`.

**Layers.**
- `Finance/` is pure and unit-tested, with no EF and no DI: `Recurrence` (dates), `Projection` (running balance), `BillSchedule` (due dates merged with recorded payments), `Amortization`/`LoanMath`/`LoanPayments` (loans). Put new money logic here and test it.
- `Services/LedgerService` is the only data access for ledger data. It's scoped per circuit and creates a short-lived `DbContext` per call via `IDbContextFactory`. Pages load data through it, then call `Finance` functions.
- Dialogs edit a `Copy()` of an entity. `LedgerService.SaveAsync<T>` copies scalar values onto a tracked row, so navigation properties on the passed object are never attached.
- App-wide singletons: `AppSettingsStore` (key/value `AppSettings` table), `RegistrationPolicy`, `EmailSettingsStore`, `SmtpEmailService`.

## Data ownership (security boundary)

- All financial data belongs to a `Ledger`. Each user has one personal ledger and at most one sample ledger (`IsSample`; unique on `OwnerId, IsSample`). `ApplicationUser.ActiveLedgerId` selects which one is shown; `LedgerService` verifies it belongs to the current user (`ICurrentUser`).
- Every `LedgerService` query and write must filter by the active ledger id, and must also validate referenced ids (a bill's account/loan, an income's account) with `EnsureInLedgerAsync`. Add a case to `LedgerServiceTests` (in-memory SQLite) for any new entity or write path.
- Ledger and user deletion cascade in the database. Switching ledgers force-reloads the page (`NavigateTo(..., forceLoad: true)`) rather than refreshing components.
- Sample data (`SampleData`) is opt-in only and lives in the sample ledger; never seed data automatically.
- Admins manage app settings and roles only; they can't read other users' ledgers.

## Domain rules

- Recurring dates are always computed from the start date, so month-end anchors don't drift (Jan 31 → Feb 28 → Mar 31).
- An account balance is "as of the start of" `BalanceAsOf`; projections apply bills/income dated on or after it. `BillOccurrence` stores per-due-date facts (amount override, paid date, loan payment kind); paid bills project on their paid date.
- Overdue = unpaid, not autopay, due before today, and on/after the pay-from account's balance date.
- Bills belong to an optional `Category` (per ledger, name unique ignoring case via a NOCASE collation, optional palette color from `CategoryChip.Palette`). `LedgerService.SaveCategoryAsync` throws `LedgerValidationException` (message safe to show) for blank/duplicate names; deleting a category sets its bills' `CategoryId` to null.
- Migrations that replace a column with a relation must copy data with `migrationBuilder.Sql` before the drop. EF's SQLite table rebuilds run where the rebuilding operation appears, so order the `Up` operations explicitly (see `BillCategories`) and check `dotnet ef migrations script`.
- Negative/low-balance alerts only fire when the balance crosses the line (a credit card that starts negative isn't "going negative").

## Loans

- A bill linked to a loan (`Bill.LoanId`) is a `MonthlyPayment` or `ExtraPrincipal` bill; `BillOccurrence.PaymentKind` overrides it for one payment (stored only when it differs).
- Monthly payments are assumed made on schedule. Only extra principal changes the balance (`LoanPayments`): an extra-kind payment's whole amount, or whatever a monthly payment exceeds its **bill's** expected amount by (so escrow included in the bill isn't counted). Autopay loan bills count on their due dates without being marked paid.
- `LoanMath` requires the extra payments explicitly (`LoanPayments.ExtrasFor(loan, bills, today)`); pass them everywhere a balance or schedule is displayed. `ActualSchedule` starts from the lender-reported balance (applying only extras after `BalanceAsOf`) or from the original terms.
- `Amortization.Build(additionalPayments:)` applies each one-off payment after the scheduled payment on or before its date; `AmortizationSchedule.BalanceOn` accounts for its exact date. Payments are rounded to the cent, so the final payment absorbs the residue (e.g. $1,200.14 vs $1,199.10 on 200k/6%/30y).

## Auth, admins and email

- ASP.NET Core Identity with roles. The first registered account becomes `Admin` (`AdminService.OnUserCreatedAsync`, called *before* sign-in so the role is in the cookie). The last admin can't be demoted or delete their account while other users exist. Role changes update the security stamp, and `SecurityStampValidatorOptions.ValidationInterval` is 1 minute.
- `RegistrationPolicy`: admins can close sign-ups, but registration is always open when no accounts exist; `Register.razor` re-checks on POST.
- Email (MailKit SMTP) is only used for password reset; sign-up has no email confirmation, and a successful reset marks the email confirmed. Settings resolve in `EmailSettingsStore`: the `Email` configuration section wins if it sets `Host` (the admin page becomes read-only); otherwise admin-saved JSON in `AppSettings`, with the password encrypted by Data Protection (app name "Ledgerly", keys in the Windows profile). Never put secrets in appsettings.json.
- Build links in emails with `EmailLinks` (effective `PublicBaseUrl`), never from the request host.
- Local email testing: `smtp4dev` (dotnet tool; SMTP 2525, web UI/API 5080) with host `localhost`, port 2525, security `None`.

## Gotchas

- **MudBlazor 9** differs from older versions and from most online examples. Verify parameters in `~/.nuget/packages/mudblazor/<version>/lib/net10.0/MudBlazor.xml`. Known differences: `MudChart<T>` with `List<ChartSeries<T>>`; `MudTabs.TabPanelsClass`; `MudMenu` `ActivatorContent` must call `context.ToggleAsync` itself; there's no `OnClickStopPropagation` (wrap the element in `<span @onclick:stopPropagation="true">`).
- `MudDatePicker` binds `DateTime?`; the model uses `DateOnly`, so use `Shared/DateOnlyPicker`.
- Razor attribute values containing `""` (e.g. `SortBy="new Func<...>(b => b.X ?? "")"`) break parsing; move the lambda into a field.
- SQLite stores `decimal` as TEXT, so sort and aggregate money in memory after loading, not in LINQ-to-SQL.
- On this Windows machine, PowerShell `Get-Content -Raw`/`Set-Content` round-trips corrupt UTF-8 (`·` becomes `Â·`). Edit files with the Edit tool, or use `[IO.File]` with an explicit UTF-8 encoding.
