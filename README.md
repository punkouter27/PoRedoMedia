# PoRedoMedia

Turn a photo or a short video into something funnier: meme captions and sounds, spoken roasts, restyled pictures,
AI video, comic strips made from a pasted conversation, and short narrated movies built from your own clips.

**Live:** https://app-poredomedia2.azurewebsites.net · **Status:** https://app-poredomedia2.azurewebsites.net/diag

Sign-in is a Microsoft account on an allow-list; signed-out visitors see the sign-in page and the public `/diag`
status page.

## What is in it

| Page | What it does |
|---|---|
| `/` Create | Pick a photo or video (or a Library item), choose functions (meme caption, roast, restyle, photo to video, Meme-ify for video…) and run them; progress streams live over SignalR. |
| `/gallery` Library | Everything you made: download, GIF and social-size exports, share links, delete; shows storage used. |
| `/comic` Comic-ify | Paste a chat or email thread; get a lettered comic strip and a narrated video of it. |
| `/movie` Create Movie | Your clips become a narrated movie with intro, outro and Meme-ify on every clip; download as a YouTube/TikTok package or publish directly. **Just make it** needs nothing but the clips: the app watches and listens to them, decides what kind of video they make (kids, pets, trip, party, sport, how-to…) and from that the series, pace, music, narrator and roast; keeps each clip's best parts (or whole clips, if you choose); orders them by when and where they were filmed or as a story; frames mixed upright and wide footage to one shape, steadies shaky shots and evens out brightness; lets people who talk be heard and narrates the rest; cuts on the music's beat with a whip only where the scene changes; makes a full video or an upright Short; and leaves out black or unusable clips before the final render. |
| `/diag` System status | Plain-words health of the server, storage and every AI or publishing service it uses. |

A .NET MAUI phone app (`src/PoRedoMedia.Mobile`, Android/iOS) uploads from the camera roll with resumable uploads and
follows runs live.

## Projects

```
src/PoRedoMedia.Api      ASP.NET Core host: endpoints (vertical slices under Features/), run pipeline, storage, AI providers
src/PoRedoMedia.Client   Blazor WebAssembly UI (Radzen), served by the Api
src/PoRedoMedia.Shared   wire DTOs, enums, config keys, validators (shared by Api, Client and Mobile)
src/PoRedoMedia.Movie    Create Movie's engine (script, pictures, FFmpeg render)
src/PoRedoMedia.Mobile   MAUI phone client (not in the .slnx; CI builds the Android target)
tests/PoRedoMedia.Unit   117 fast tests, no I/O
tests/PoRedoMedia.Integration   50 tests against a throwaway Azurite (Docker) and real ffmpeg
tests/PoRedoMedia.E2EAPI 13 in-process API contract tests
tests/PoRedoMedia.E2EUI  19 Playwright tests against a running app
infra/                   Bicep for the Azure resources the app runs on
docs/                    architecture.md, publishing-setup.md
SCREENSHOTS/             before/after pages of UI changes ({YYMMDD}-{feature}.html + .png), made by scripts/screenshots.ps1
```

More detail: [docs/architecture.md](docs/architecture.md). Rules for agents and contributors: [AGENTS.md](AGENTS.md).

## Run it locally

Needs the .NET SDK from `global.json`, Docker Desktop (Azurite) and, for video work, `ffmpeg` on PATH.

```bash
docker compose up -d                          # Azurite: local Blob + Table storage
dotnet run --project src/PoRedoMedia.Api      # http://localhost:4100
```

Development uses Azurite for storage and reads AI keys from Key Vault `kv-poshared2` through your `az login`. For a
key-free run with stand-in AI, set `ASPNETCORE_ENVIRONMENT=Test` and `Mocks__UseMockAi=true` (that is what
`scripts/run-e2e.ps1` does), then sign in by opening `/dev-login?email=you@example.com&returnUrl=/` (Development and Test only: a 404 in Production, and
it returns only to a path on this site). The sign-in page itself has one button, Microsoft's, in every environment.

Tests:

```bash
dotnet test tests/PoRedoMedia.Unit
dotnet test tests/PoRedoMedia.Integration     # skips with a message when Docker (or ffmpeg) is missing
dotnet test tests/PoRedoMedia.E2EAPI
pwsh scripts/run-e2e.ps1                      # publishes Release, starts a throwaway Azurite + that build on free ports, runs the Playwright tier
```

## How it deploys

A push to `master` runs `.github/workflows/deploy.yml`: format check, Release build (warnings are errors), Unit and
Integration tests, publish, bundle a static ffmpeg and fonts (`scripts/ci-package.ps1`), deploy to App Service
`app-poredomedia2` with GitHub OIDC, then wait until `/health/live` reports the pushed commit in its `X-Commit`
header. A separate `mobile` job builds the Android app without blocking the deploy. Infrastructure
(`infra/main.bicep`) is not deployed by CI; preview changes with:

```bash
az deployment group what-if -g PoRedoMedia -f infra/main.bicep -p infra/main.bicepparam
```

Azure: resource group `PoRedoMedia` (West US 3) holds the F1 Linux plan, the App Service and storage account
`stporedomedia2`; secrets are in the shared Key Vault `kv-poshared2` (resource group `PoShared`) as `PoRedoMedia--*`.
Roles and the CI identity are set up by `scripts/ensure-azure-access.ps1`.
