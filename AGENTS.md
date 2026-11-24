# PoRedoMedia — agent guide

## 1. Standard

This repo owns every file in it. Nothing here is generated from, synced with, or required to stay identical to
another repository. Headers like "SAME FILE IN EVERY PO …", "Generated from po-shared … DO NOT EDIT", and scripts
that copy files into or check other repos are forbidden. Only Azure resources in the `PoShared` resource group
may be shared.

### Po Web App Standard

This repo is one of several independent Po apps. It follows the conventions below, but it owns every file:
nothing is generated from, synced with, or required to stay identical to another repository. Change anything
here freely; only the *conventions* are common, never the code. Azure resources may be shared through the
`PoShared` resource group; code may not.

#### Repo skeleton
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

#### Page shell (every page, signed in or out, including the sign-in page)
- Header `.po-header`, height 56px, sticky: app name top-left (`.po-header__title`, links to `/`).
- Top-right `.po-header__actions`: signed-in user's **email** (`.po-header__email`, full email, ellipsis at 16ch on
  phones) — or, signed out, one **Sign in** button `class="po-link-btn filled small"` (same style on every page except
  the sign-in page itself) — then the settings gear. Apps without OAuth show nothing in place of the email (no fake
  identity chips).
- **Sign-in page** (`/login`): the app's name as its `h1` and the sign-in button, nothing else; no navigation, no
  tagline, no second Sign in in the header. Navigation appears only once signed in. It renders inside `MainLayout`,
  the app's one layout (no separate sign-in layout). Exactly one button in every environment: the Dev/Test sign-in is
  the URL `/dev-login?email=…&returnUrl=/path` (404 in Production; returns only to a site-relative path), not a button.
- Version badge `.po-version` bottom-right showing `vYYMMDD`, **in the page's flow, never fixed**: it is the last item
  of the scrolling column (`RadzenBody`) as a `<footer>` landmark, in a flex column, so it sits in
  the bottom-right corner of a short page and after the content of a long one. It can never cover content or the
  phone tab bar (a row of its own in the layout grid).
- Colours only from `wwwroot/css/po-tokens.css` (`--po-*`). All text ≥ 4.5:1 contrast in light AND dark mode
  (status banners: use `--po-text` on `--po-*-soft` backgrounds, never warning-on-peach).
- Login-only data must not be requested for signed-out visitors (no 401s in the console on public pages), and
  actions a signed-out visitor cannot perform are hidden.

#### Version
- `Directory.Build.props` stamps ONE assembly metadata key: `BuildVersion` = UTC `yyMMdd`. No `BuildStamp`/`BuildDate`
  aliases. Every reader (header badge component, `/api/diag`, `/health` headers) reads `BuildVersion`.

#### Components (owner decision 2026-11-21: "Radzen wins, convert")
- Every interactive control is a Radzen component: buttons, text fields, checkboxes, sliders, select bars, colour
  picker, dialogs, notifications. No other component library, no raw `<button>`/`<input>`/`<select>`/`<textarea>`.
- Controls that draw themselves (tiles, steps, thumbnails, lyric lines, disclosure rows) are
  `RadzenButton ButtonStyle="Base" Variant="Flat" class="po-plain <name>"`; `css/app.css` resets Radzen's look for
  `po-plain`, and the component's stylesheet styles it with `::deep .rz-button.<name>`. A tile that ticks is a `div`
  with `@onclick` holding a `RadzenCheckBox` (named with `aria-label`) whose own click is stopped
  (`@onclick:stopPropagation`), so it never toggles twice.
- Left as they are, on purpose: Blazor's `InputFile` (hidden; a RadzenButton opens it, because Radzen's upload cannot
  hand the page the file stream), `<details>/<summary>` disclosures (account menu, "What the AI saw"), and native
  `<audio>`/`<video>` players.

#### Health + /diag contract
- `GET /health/live` (liveness, returns header `X-Commit: <sha>`), `GET /health` (readiness), `GET /api/diag` →
  ```
  DiagReportDto(string Status, string Environment, string Version, DateTimeOffset CheckedAt, double CheckedMs,
                DiagCheckDto[] Checks)
  DiagCheckDto(string Name, string Group /* "api" | "data" | "service" */, string Label, string Status
               /* Healthy|Degraded|Unhealthy|Off */, string Purpose, string Detail, double LatencyMs, string? Problem)
  ```
- UI route `/diag` ONLY (delete other diagnostics pages/routes such as `/system`, `/diagnostics`, `/status`). Page
  `<title>` and `h1`: **System status**. Layout: overall banner in plain words ("Everything is working" /
  "Working, with N things degraded" / "Not working: …"), then three sections in this order: **API**, **Data**,
  **Services**; each row = label, purpose, status pill, latency ms, problem/detail text. Every external dependency
  the app really calls appears as a check, **probed live** (never "configured" alone). Anonymous, nothing secret in
  the payload.

#### Workflow (`.github/workflows/deploy.yml`, the only one)
push to master → format check → build (Release, warnings as errors) → Unit tests → Integration tests (Testcontainers /
Azurite; Docker is available on ubuntu-latest) → publish → deploy → confirm `/health/live` X-Commit = this SHA.
If the repo has a MAUI `src/<App>.Mobile`, a second job in the SAME file (`mobile`, `needs: []`, not blocking deploy)
installs the `maui-android` workload and builds the Android target, so the client is always compiled.

#### Tests
- Unit tier: fast, no I/O, runs in CI.
- Integration tier: runs in CI; locally it SKIPS with a clear message when Docker is not running (never a wall of
  `DockerUnavailableException` failures).
- E2E/UI tiers: run locally against a running app; when no app URL / browser is available they SKIP with a clear
  message. They are not part of CI.
- Docs must state real counts, not caps that are out of date.

#### Azure naming
App Service `app-<repo lowercase>`; per-app resources named `<type>-<repo lowercase>`; shared things only in `PoShared`.

## 2. Concurrent work

Several sessions (people and agents) commit to this repo, sometimes at the same time.
- Before every commit and every push: `git fetch origin && git rebase --autostash origin/master`.
- Stage only the files you changed (`git add <paths>`); never `git add -A` / `git add .`; check `git diff --cached`.
- Never revert, reformat or overwrite changes you didn't make; keep the owner's recent decisions (e.g. features
  removed on purpose stay removed, such as the Settings page and gear).
- Don't commit or sync from the IDE while an agent is working in this folder.
- Pushing `master` deploys production; changes to `.github/workflows/*` need a token with the `workflow` scope.

## 3. Verification

One command sequence verifies the repo (format, build, Unit, Integration, E2E API, then E2E UI on the trimmed Release
publish):

```powershell
dotnet format PoRedoMedia.slnx --verify-no-changes && dotnet build PoRedoMedia.slnx -c Release && dotnet test tests/PoRedoMedia.Unit -c Release --no-build && dotnet test tests/PoRedoMedia.Integration -c Release --no-build && dotnet test tests/PoRedoMedia.E2EAPI -c Release --no-build && pwsh scripts/run-e2e.ps1 -FetchFFmpeg
```

All run from the repo root and checked on 2026-11-21 (.NET SDK 10.0.403 via `global.json` 10.0.400 + latestFeature).

| What | Command | Needs / without it |
|---|---|---|
| Format | `dotnet format PoRedoMedia.slnx --verify-no-changes` | — |
| Build | `dotnet build PoRedoMedia.slnx -c Release` | warnings are errors |
| Unit (117) | `dotnet test tests/PoRedoMedia.Unit -c Release --no-build` | nothing |
| Integration (50) | `dotnet test tests/PoRedoMedia.Integration -c Release --no-build` | Docker (Testcontainers Azurite) and ffmpeg on PATH for 9 video tests; each skips with a "Skipped: …" message when missing. Any static ffmpeg build works (e.g. the gyan.dev "essentials" zip, its `bin` put first on PATH for the shell that runs the tests and the app) |
| E2E API (13) | `dotnet test tests/PoRedoMedia.E2EAPI -c Release --no-build` | nothing (in-process host, mock AI) |
| E2E UI (19) | `pwsh scripts/run-e2e.ps1 [-Filter <name>] [-FetchFFmpeg]` | Docker; publishes the trimmed Release build (`dotnet publish -c Release`, what deploys), starts a throwaway Azurite container and that publish (Test env, mock AI) on free ports it picks (never the compose Azurite on 10000 nor a fixed app port, so concurrent sessions do not collide), runs Playwright with `E2E_REQUIRE_APP=1` (nothing may skip), then removes the container. It prints the ffmpeg it uses (`-FFmpegDir`, `FFMPEG_DIR`, PATH, or a portable copy in `%LOCALAPPDATA%\PoRedoMediafmpeg` that `-FetchFFmpeg` downloads; nothing is installed system-wide). Plain `dotnet test tests/PoRedoMedia.E2EUI` skips every test with a message when no app answers at `E2E_BASE_URL` or Chromium is missing; 5 video tests also skip without ffmpeg |
| Run locally | `docker compose up -d` then `dotnet run --project src/PoRedoMedia.Api` | → http://localhost:4100 (Development: Azurite + Key Vault keys via `az login`) |
| Deploy | push to `master` → `.github/workflows/deploy.yml` (job `deploy`; job `mobile` builds Android, non-blocking) | `*.md` and `SCREENSHOTS/**` pushes do not deploy |
| Deploy check | `curl -sI https://app-poredomedia2.azurewebsites.net/health/live` shows `X-Commit: <pushed sha>` | the workflow waits for this |
| Live | https://app-poredomedia2.azurewebsites.net · https://app-poredomedia2.azurewebsites.net/diag | |
| Screenshots | `pwsh scripts/screenshots.ps1 -Feature <name> [-Before <git ref, default HEAD~1> \| -BeforeDir <PNGs>] [-Routes /login,/diag,in:/,in:/gallery] [-Notes ...]` | Docker; publishes the Release build of the before ref (temporary `git worktree`) and of the working tree, runs each locally (Test env, mock AI, throwaway Azurite, free ports; never the live site), captures every route at 1366×768 and 390×844 with the E2EUI project's Playwright (`in:` = signed in via /dev-login), writes `SCREENSHOTS/{YYMMDD}-{feature}.html` (before \| after per screen) and one annotated side-by-side `.png`, then removes the worktree, app and container |
| Infra preview | `az deployment group what-if -g PoRedoMedia -f infra/main.bicep -p infra/main.bicepparam` | read-only; never deployed by CI |

## Repo-specific notes

PoRedoMedia turns photos and videos into memes, roasts, comics and short movies (Blazor WebAssembly hosted by an
ASP.NET Core API, FFmpeg, several AI providers). What it is and how to run it: [README.md](README.md). How the code is
laid out: [docs/architecture.md](docs/architecture.md). Publishing keys: [docs/publishing-setup.md](docs/publishing-setup.md).

### Deliberate deviations

| Deviation | Why | Commit sha / date | How to undo |
|---|---|---|---|
| No Settings gear or page; switches are fixed defaults in `Client/Services/AppSettings`; the email opens a small menu with Sign out | owner's call | `4937fc6` 2026-11-20 | bring the settings page back |
| Radzen exceptions: hidden Blazor `InputFile` (a RadzenButton opens it), `<details>/<summary>` disclosures, native `<audio>`/`<video>` | Radzen's upload cannot hand the page the file stream; native players | `d9e4574` 2026-11-21 | — |
| /diag: services switched off on purpose show a grey **Off** pill, not counted (production today: App Insights, Pexels, YouTube, TikTok) | not set up here; Off is honest, Degraded was not | `9db0e8c` 2026-11-21 | set the keys / connection string |
| Sign-in page is only the name and the button; the version stamp never sits on content on phones | overrides the older shell layout | `d9e4574` 2026-11-21 | `git revert d9e4574` (layout part) |
| Azure names `app-poredomedia2`, `stporedomedia2`, `asp-PoRedoMedia-f1` (standard `app-poredomedia`) | renaming means recreating and moving data | — | owner-approved recreate |

**Hosting:** App Service F1 (Free, Linux, `asp-PoRedoMedia-f1`): 165 MB of data out and 60 CPU-minutes per day, 1 GB
disk, no Always On. Never run test suites against the live site; live checks = at most 6 page loads + /diag + X-Commit.

### /diag here
`Common/Health/HealthChecks.cs` registers the rows (each with a 10 s timeout); `Common/Health/ServiceProbes.cs` holds the
live probes. `/health` and `/api/diag` read one cached report (`Common/Health/HealthReportCache.cs`, at most one run
every 60 s, shared by concurrent callers), so two loads within a minute show the same `checkedAt`. Each asks
the service something free: Gemini reads the vision model's description, Azure OpenAI and AI Foundry list their models,
Key Vault lists one page of secret names with the app's identity, Microsoft sign-in reads the tenant's OpenID document,
Application Insights and Pexels are probed only when set up, YouTube and TikTok reach their sign-in servers only when
their app keys are set. A service switched off on purpose (not set up here, or a stand-in in Test) is not called and
shows **Off**: a grey neutral pill (`.po-badge--off`) that does not count toward the banner or the overall status
(`ServiceProbe.Off`, asserted by E2EAPI `HealthApiTests` and E2EUI `DiagUiTests`). Production today: Application
Insights, Pexels, YouTube and TikTok are Off (no connection string / keys set).

### Working rules
- **Stay on `master`:** commit directly to `master`; create branches only when asked. Short, natural commit messages.
- **Startup verification:** after a code change, restart the app and check it starts.
- **Zero warnings:** warnings are errors (`Directory.Build.props`), so `dotnet build` is also the lint step.
- **Fully automated:** run builds, scripts and setup commands yourself; never ask the user to click through a portal.
- **Secrets:** no `dotnet user-secrets`. Local values go in `appsettings`; deployed secrets live in Key Vault
  `kv-poshared2` (PoShared), names prefixed `PoRedoMedia--` (`--` becomes `:`).
- **Keep docs true:** update README.md, docs/ and this file in the same change when dependencies, config keys,
  endpoints or test counts change.
- **UI previews:** for a visual change, run `pwsh scripts/screenshots.ps1 -Feature <name> [-Before <ref>]`: it writes
  `SCREENSHOTS/{YYMMDD}-{feature}.html` and the side-by-side `.png` (README links the folder).
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
pwsh scripts/run-e2e.ps1 [-Filter x] [-FetchFFmpeg]    # E2EUI against the Release publish: throwaway Azurite + the app on free ports (Test env, mock AI), Playwright
dotnet run --project src/PoRedoMedia.Api -- seed-sounds   # fills the sound library, then exits (startup does this too)
pwsh scripts/mobile-dev.ps1 -ApiPort 4100 -Run         # the MAUI phone app against the local API
pwsh scripts/screenshots.ps1 -Feature x [-Before ref]  # SCREENSHOTS/{YYMMDD}-x.html + .png, before/after at 1366 and 390, local only
```

### Tests (real counts, 2026-11-23)
| Tier | Project | Cases | Where it runs |
|---|---|---|---|
| Unit | `tests/PoRedoMedia.Unit` | 117 | CI + locally; no I/O |
| Integration | `tests/PoRedoMedia.Integration` | 50 | CI (Docker + ffmpeg installed) + locally. `[DockerFact]` skips with "Docker is not running…" without Docker; `[FFmpegFact]`/`[DockerFFmpegFact]` skip with "ffmpeg is not on PATH…" without ffmpeg |
| E2E API | `tests/PoRedoMedia.E2EAPI` | 13 | locally; in-process host, mock AI, no Docker |
| E2E UI | `tests/PoRedoMedia.E2EUI` | 19 | locally against the Release publish (`scripts/run-e2e.ps1`); `[LiveServerFact]` skips with a message when no app answers at `E2E_BASE_URL` or Playwright's Chromium is missing |

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
