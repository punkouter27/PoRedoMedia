# PoRedoMedia — agent guide

## Po Web App Standard

This repo is one of several independent Po apps. It follows the conventions below, but it owns every file:
nothing is generated from, synced with, or required to stay identical to another repository. Change anything
here freely; only the *conventions* are common, never the code. Azure resources may be shared through the
`PoShared` resource group; code may not.

### Repo skeleton
```
README.md          what the app is, live URL, how to run locally, how it deploys
AGENTS.md          this standard + repo-specific notes (CLAUDE.md just points here)
CLAUDE.md          one line: "Read AGENTS.md."  (plus repo-specific notes only if unavoidable)
<App>.slnx  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
src/<App>.Api  src/<App>.Client  src/<App>.Shared  [src/<App>.Mobile]
tests/<App>.Unit  tests/<App>.Integration  [tests/<App>.E2E...]
infra/main.bicep  infra/main.bicepparam  [infra/modules/*.bicep]
scripts/           setup / helper scripts (no infra/*.ps1)
docs/              lowercase; everything else that is prose
.github/workflows/deploy.yml   the ONLY workflow
```
No `.cursorrules`, `.copilotignore`, `SPEC.md`, `tasks/`, `DOCS/` at the root (fold useful content into docs/).
`SCREENSHOTS/` is allowed only if README references it.

### Page shell (every page, signed in or out, including the sign-in page)
- Header `.po-header`, height 56px, sticky: app name top-left (`.po-header__title`, links to `/`).
- Top-right `.po-header__actions`: signed-in user's **email** (`.po-header__email`, full email, ellipsis at 16ch on
  phones) — or, signed out, one **Sign in** button `class="po-link-btn filled small"` (same style on every page) —
  then the settings gear. Apps without OAuth show nothing in place of the email (no fake identity chips).
- Version badge `.po-version` fixed bottom-right showing `vYYMMDD`. It must never cover content: `main` gets
  `padding-bottom: calc(var(--po-space-6) + env(safe-area-inset-bottom))`, and when a mobile bottom nav exists the
  badge sits above it (`--po-stamp-lift: <nav height>`).
- Colours only from `wwwroot/css/po-tokens.css` (`--po-*`). All text ≥ 4.5:1 contrast in light AND dark mode
  (status banners: use `--po-text` on `--po-*-soft` backgrounds, never warning-on-peach).
- Login-only data must not be requested for signed-out visitors (no 401s in the console on public pages), and
  actions a signed-out visitor cannot perform are hidden.

### Version
- `Directory.Build.props` stamps ONE assembly metadata key: `BuildVersion` = UTC `yyMMdd`. No `BuildStamp`/`BuildDate`
  aliases. Every reader (header badge component, `/api/diag`, `/health` headers) reads `BuildVersion`.

### Health + /diag contract
- `GET /health/live` (liveness, returns header `X-Commit: <sha>`), `GET /health` (readiness), `GET /api/diag` →
  ```
  DiagReportDto(string Status, string Environment, string Version, DateTimeOffset CheckedAt, double CheckedMs,
                DiagCheckDto[] Checks)
  DiagCheckDto(string Name, string Group /* "api" | "data" | "service" */, string Label, string Status
               /* Healthy|Degraded|Unhealthy */, string Purpose, string Detail, double LatencyMs, string? Problem)
  ```
- UI route `/diag` ONLY (delete other diagnostics pages/routes such as `/system`, `/diagnostics`, `/status`). Page
  `<title>` and `h1`: **System status**. Layout: overall banner in plain words ("Everything is working" /
  "Working, with N things degraded" / "Not working: …"), then three sections in this order: **API**, **Data**,
  **Services**; each row = label, purpose, status pill, latency ms, problem/detail text. Every external dependency
  the app really calls appears as a check. Anonymous, nothing secret in the payload.

### Workflow (`.github/workflows/deploy.yml`, the only one)
push to master → format check → build (Release, warnings as errors) → Unit tests → Integration tests (Testcontainers /
Azurite; Docker is available on ubuntu-latest) → publish → deploy → confirm `/health/live` X-Commit = this SHA.
If the repo has a MAUI `src/<App>.Mobile`, a second job in the SAME file (`mobile`, `needs: []`, not blocking deploy)
installs the `maui-android` workload and builds the Android target, so the client is always compiled.

### Tests
- Unit tier: fast, no I/O, runs in CI.
- Integration tier: runs in CI; locally it SKIPS with a clear message when Docker is not running (never a wall of
  `DockerUnavailableException` failures).
- E2E/UI tiers: run locally against a running app; when no app URL / browser is available they SKIP with a clear
  message. They are not part of CI.
- Docs must state real counts, not caps that are out of date.

### Azure naming
App Service `app-<repo lowercase>`; per-app resources named `<type>-<repo lowercase>`; shared things only in `PoShared`.

## Repo-specific notes

PoRedoMedia turns photos and videos into memes, roasts, comics and short movies (Blazor WebAssembly hosted by an
ASP.NET Core API, FFmpeg, several AI providers). What it is and how to run it: [README.md](README.md). How the code is
laid out: [docs/architecture.md](docs/architecture.md). Publishing keys: [docs/publishing-setup.md](docs/publishing-setup.md).

### Working rules
- **Stay on `master`:** commit directly to `master`; create branches only when asked. Short, natural commit messages.
- **Startup verification:** after a code change, restart the app and check it starts.
- **Zero warnings:** warnings are errors (`Directory.Build.props`), so `dotnet build` is also the lint step.
- **Fully automated:** run builds, scripts and setup commands yourself; never ask the user to click through a portal.
- **Secrets:** no `dotnet user-secrets`. Local values go in `appsettings`; deployed secrets live in Key Vault
  `kv-poshared2` (PoShared), names prefixed `PoRedoMedia--` (`--` becomes `:`).
- **Keep docs true:** update README.md, docs/ and this file in the same change when dependencies, config keys,
  endpoints or test counts change.
- **UI previews:** for a visual change, save before/after HTML mockups to `SCREENSHOTS/{YYMMDD}-{feature}.html`
  (README links the folder).
- **Problems found:** propose a fix and wait for the user's approval before acting on it; say so when a change removes
  more than 100 lines.

### Commands
```bash
docker compose up -d                                   # Azurite (local Tables + Blobs); the app hangs at startup without it
dotnet run --project src/PoRedoMedia.Api               # http://localhost:4100 (also serves the Blazor client)
dotnet format PoRedoMedia.slnx --verify-no-changes     # what CI checks first
dotnet build PoRedoMedia.slnx -c Release               # warnings are errors
dotnet test tests/PoRedoMedia.Unit                     # add --filter "FullyQualifiedName~MemeRenderingTests" for one class
dotnet test tests/PoRedoMedia.Integration              # needs Docker (Testcontainers Azurite); video tests also need ffmpeg
pwsh scripts/run-e2e.ps1 [-Filter x]                   # E2EUI: starts Azurite + the app (Test env, mock AI), runs Playwright
dotnet run --project src/PoRedoMedia.Api -- seed-sounds   # fills the sound library, then exits (startup does this too)
pwsh scripts/mobile-dev.ps1 -ApiPort 4100 -Run         # the MAUI phone app against the local API
```

### Tests (real counts, 2026-11-20)
| Tier | Project | Cases | Where it runs |
|---|---|---|---|
| Unit | `tests/PoRedoMedia.Unit` | 100 | CI + locally; no I/O |
| Integration | `tests/PoRedoMedia.Integration` | 50 | CI (Docker + ffmpeg installed) + locally. `[DockerFact]` skips with "Docker is not running…" without Docker; `[FFmpegFact]`/`[DockerFFmpegFact]` skip with "ffmpeg is not on PATH…" without ffmpeg |
| E2E API | `tests/PoRedoMedia.E2EAPI` | 8 | locally; in-process host, mock AI, no Docker |
| E2E UI | `tests/PoRedoMedia.E2EUI` | 16 | locally against a running app (`scripts/run-e2e.ps1`); `[LiveServerFact]` skips with a message when no app answers at `E2E_BASE_URL` or Playwright's Chromium is missing |

There are no test caps; keep the counts above current.

### CI/CD and Azure
- `.github/workflows/deploy.yml` is the only workflow. A push to `master` runs format check → Release build → Unit →
  Integration (ffmpeg installed by apt) → publish → `scripts/ci-package.ps1` (bundles a static Linux ffmpeg and fonts) →
  deploy → waits for `/health/live` to report the pushed commit in `X-Commit`. A red step stops the deploy and the live
  site keeps its last good build. A second job, `mobile` (`needs: []`), installs `maui-android` and builds the Android
  target; it never blocks the deploy.
- Azure sign-in is GitHub OIDC (the `gh-poredomedia-deploy` app); its IDs are repo **variables** `AZURE_CLIENT_ID`,
  `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, plus `AZURE_WEBAPP_NAME` and `AZURE_RESOURCE_GROUP`. Never move them to
  secrets or write resource names into the workflow.
- Every role the app and CI need lives in `scripts/ensure-azure-access.ps1`; a role granted by hand and not added there
  is a bug.
- `infra/main.bicep` + `infra/main.bicepparam` describe what is deployed today (resource group `PoRedoMedia`, West US 3):
  App Service plan `asp-PoRedoMedia-f1` (Linux F1), App Service `app-poredomedia2` (.NET 10, system identity), storage
  account `stporedomedia2`. Check changes with `az deployment group what-if`; infra is not deployed by CI.
- **Naming exception (standard: `app-poredomedia`):** the live resources keep their current names (`app-poredomedia2`,
  `stporedomedia2`, `asp-PoRedoMedia-f1`); renaming means recreating them and moving data, which is the owner's call.
- The plan is F1 (Free): no Always On, 1 GB disk, cold starts of several seconds. Create Movie keeps no video on the
  server for this reason.
- Shared Azure resources are only in `PoShared`: Key Vault `kv-poshared2` (secrets `PoRedoMedia--*`).
