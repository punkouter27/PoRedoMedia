# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

@AGENTS.md

## Commands

```bash
docker compose up -d                                   # Azurite (local Tables + Blobs); the app hangs at startup without it
dotnet run --project src/PoRedoMedia.Api               # http://localhost:4100 (also serves the Blazor client)
dotnet build PoRedoMedia.slnx                          # warnings are errors, so this is the lint step too
dotnet test tests/PoRedoMedia.Unit --filter "FullyQualifiedName~MemeRenderingTests"   # one class / test
dotnet run --project src/PoRedoMedia.Api -- seed-sounds   # fills the sound library, then exits (the app also does this at startup)
```

- `IntegrationTests` start their own throwaway Azurite through Testcontainers (`AzuriteFixture`), never the compose
  one. `[DockerFact]`/`[DockerTheory]` skip when Docker is down, so a "pass" may mean "skipped".
- `E2EUI` is Playwright, gated by `[LiveServerFact]` (skips unless something listens at `E2E_BASE_URL`). Use
  `scripts/run-e2e.ps1 [-Filter x]`, which starts Azurite and the app; `HEADED=1` shows the browser.
- Test budgets (Unit ≤ 100, Integration ≤ 50, E2E API ≤ 25, E2E UI ≤ 25) are enforced by
  `scripts/check-test-budgets.ps1`, which counts cases via `--list-tests` and needs a Release build first.
- CI (`.github/workflows/ci.yml`) runs only the Release build and unit tests; `deploy.yml` ships each green master build.

## Architecture

**Projects.** `PoRedoMedia.Movie` is Create Movie's engine, brought over from PoAutoRobo's core (see below).
`PoRedoMedia.Api` is the ASP.NET Core host and all server logic; it also hosts
`PoRedoMedia.Client` (Blazor WebAssembly, prerender off, because the client owns the only UI DI container).
`PoRedoMedia.Shared` holds wire DTOs, enums (`MediaFunction`), `ConfigKeys`, `FunctionStack` rules and catalogs.
Wire enums travel as strings over both HTTP and SignalR. Shared is `IsAotCompatible` and the client `IsTrimmable` with
the AOT analyzer on, so trim- or AOT-unsafe code fails the build; JSON goes through the source-generated `WireJson`.

**Api layout.** `Features/<Slice>/` are vertical slices (Media, Runs, Auth, Render, Sharing, one folder per image
function, and `Video/` for the one step that runs Meme-ify, Insult roast and Auto-captions together; its meme sounds and roast lines take turns, `TurnTaking`, and the renderer levels every clip to -16 LUFS); each exposes
`MapX(this IEndpointRouteBuilder)` called from `Program.cs`. `Common/` holds cross-slice contracts, strongly typed IDs,
storage, AI providers (`Common/Ai`, including the video vision and speech clients) and `ImageBytes` (all still-image
decode/encode, on SkiaSharp). Exports of a result live under `/api/media/{id}/export/{gif|social|srt|roast}`
(`social?aspect=9x16|1x1|16x9` re-frames a video on a blurred fill; gif and social are made once by a POST that
costs a run, then kept beside the video). `Features/Preferences` serves `GET/PUT /api/preferences` (one per-user
string map, 16 KB cap, not yet read by the client).
`Configuration/FunctionRegistration.AddFunctions` registers AI providers and run steps.

**Run pipeline** (`Features/Runs/`). `POST /api/runs` validates with `FunctionStack.Validate`, checks each function
has a registered step, reserves the source (one run per source → 409), consumes the daily quota (→ 429), saves,
then queues on `RunDispatcher` (a BackgroundService over a Channel, one run at a time per process). `RunExecutor`
orders functions by `FunctionStack.InRunOrder` (enum declaration order, not the user's order) and finds the
`IRunStep` that handles each. Steps chain through `RunContext.Current`: `AddOutputAsync(output, becomesCurrent: true)`
feeds the next step. Throw `RunStepException` for a message the user should see; a failed run with no outputs is
refunded. Each finished step is recorded as a `StepTelemetryDto` (seconds, AI calls, tokens and cost from the
`AiTally` snapshot around it) on the run and in every progress event. Each output is then named in the background by
`IMediaSummarizer` (title of at most six words plus a one-sentence `Summary` on the media row, which Library search
matches); naming never fails a run. Progress goes out over SignalR (`RunHub` at `/hubs/run`); `HousekeepingService`
recovers runs cut short by a restart, and its first sweep per process drops retired storage (`SoundFavorites`,
`media/*/script.json`). The queue, upload rate limit and video-clip waits are in-process only (marked `ponytail:`).

**Functions only exist when configured.** A function is runnable only if its step was registered, which depends on
the config keys present (e.g. `Google:ApiKey` enables Gemini images, Omni video, Lyria, BulkStyles, PhotoToVideo). Pictures
are looked at by one `IVisionService` (Gemini, else Azure OpenAI; no user-facing picker); a description written by the
browser's own model (`Vision.description`) replaces that call. Create sends one by itself (local first) when On-device
AI is on and Florence-2 is already in the browser's cache (`LocalAiService.IsVisionReadyAsync`), and runs on the server
if the device fails; `AiCatalog.LooksAtPicture` decides which functions qualify. With both Gemini and Azure OpenAI configured, a failed
vision or chat call is asked once of the other provider (`Common/Ai/ProviderFallback`; Gemini chat goes through its
OpenAI-compatible endpoint) and the run log says so. Both roasts get a critic pass (`IRoastCritic`): a draft scoring
under 6/10 is rewritten once with the critique.
`Mocks:UseMockAi` (Test environment only; Development uses the real keys) swaps every provider for a mock and skips Key Vault. Read config only
through `Shared/ConfigKeys`. Key Vault `kv-poshared2` loads only `PoRedoMedia--*` secrets (`--` → `:`); in Development
the vault's Storage secret is skipped so local runs stay on Azurite.

**Storage.** One account; names live in `Common/Storage/StorageNames`. Table rows use the user as PartitionKey.
Everything an item owns sits under `media/{id}/` (`MediaBlobPaths`), so one prefix delete removes it. Uploads go
browser → SAS PUT to `upload{ext}` → `/confirm` validates (image size is read from the header only) and promotes to
`source{ext}`.

**Auth.** A fallback policy requires a signed-in user, so anonymous endpoints must opt out with `.AllowAnonymous()`.
Modes: fake auth via `X-Fake-User` (Dev/Test), cookie-only (throws in Production), or cookie + Entra OIDC with
`Auth:AllowedEmails`. `/dev-login?email=` (Dev/Test) creates `dev|{email}` users. JSON writes are protected only by
`RequireAntiforgeryValidation()` on route groups with the `X-CSRF-TOKEN` header; the client's
`AntiforgeryTokenHandler` retry on 400 is load-bearing.

**Settings.** `/settings` (anonymous-capable; the header's account button links to it) holds look and sound plus the
Advanced switches in `Client/Services/AppSettings` (On-device AI, Recipes, Cost estimates, Visual
effects). Every switch starts **off**, so the default app is the simple one; a switch
only hides UI, never access. Switches live in `localStorage` (`po.adv.*`, `po.fx`) and are read synchronously
through in-process JS. Signed in, `Services/PreferenceSync` pulls `/api/preferences` once from the layout (the account
wins) and PUTs the whole synced set (`po.adv.*`, `po.fx`, `po.sfx.*`, `po.recipes`) a second after any change; recipes
are read through `RecipeBook`, which skips functions that no longer exist. Every function the server can run is always offered. E2E tests turn switches on with
`SignedInPageAsync(path, "po.adv.device-ai", ...)`.

**Client pages.** Each page wraps its body in `Shared/PageBoundary` (retry without a reload, then a reload); the
layout's own `ErrorBoundary` is the last resort. Create is split into `CreateSteps`, `RecipeBar` and `RunView`; the
Library into `LibraryToolbar`, `LibraryGrid` and `MediaDrawer` (a Radzen side dialog that follows the page's
`LibrarySelection`). The app has one keydown listener (`Shared/ShortcutKeys` in the layout, `poMedia.attachKeys`);
pages take keys with `Shortcuts.Register` and dispose the handle. Components that subscribe to events, SignalR,
`DotNetObjectReference` or JS listeners release them in `Dispose`/`DisposeAsync`.

**Client effects.** `wwwroot/js/fx/` holds the visual effects (WebGPU fluid backdrop `fluid.js`, which stands in for
the glass one in `js/backdrop.js` when it can run; particles; shader reveal `dissolve.js` with the pixel-flow
`morph.js` as its fallback; run orb; confetti and download/share sparks; roast concert; track waveform
`waveform.js`; Library card tilt `tilt.js`); `sfx.js` synthesises every sound, in three themes (`po.sfx.theme`). Both
are off until switched on in Settings (`po.fx = 'on'`, `po.sfx.muted = '0'`), and effects stay off for reduced
motion. Elements that exist only for an effect carry `.po-fx-only`; effects that need no element listen on the
document instead of being called from pages. Blazor never awaits an effect: the .NET-facing entry points
(`poMorph.start`, `poConfetti.burst`, `poSfx.runEnded`) return nothing. On-device models (Florence-2 vision, Whisper
speech) live in `Client/LocalAi` and `wwwroot/js/local-ai/` (`device-tasks.js` for speech); a picture reaches the
vision worker as bytes through `[JSImport]` (`LocalAiBytes`, hence `AllowUnsafeBlocks` in the client).

**Create Movie** (`/movie`). `PoRedoMedia.Movie` is PoAutoRobo's core with its window and Azure clients taken out:
the episode and its pure edits (`EpisodeEditor`, `EditHistory`), the script writer and its prompts (`ChatScriptWriter`,
`ScriptSchemas`), pictures (`Visuals`), and the FFmpeg render (`EpisodeBuilder`: clips, intro and outro, teasers,
thumbnails). It reaches every service through a delegate (`JsonChat`, `SeeingChat`, `ImageMaker`, `INarrator`,
`Hear`), filled in `Features/Movies/MovieAi` from this app's own providers: chat from `OpenAI:*` (else `AiFoundry:*`),
voice from `AiFoundry:TtsDeployment` timed by listening back with `AiFoundry:TranscriptionDeployment`, pictures from
`IImageGenerationService`, AI video by drawing a still and animating it with `IVideoGenerationService`. Without one,
it has a stand-in (stand-in words, a tone for the voice) or the feature is not offered. Shows: `General` (the
default, a movie about anything) and the `Robot`/`Kids` presets, each with its own brand kit, shapes and prompts;
an episode's `BeatStructure` says which. `MovieStudio` stands where PoAutoRobo's view model stood: one per user,
it keeps open movies, undo history and the user's one long job **in memory** (a restart forgets undo and stops a
job), and movies as **files on this server's disk** under `Movie:Root` (default `/home/data/poredomedia/movies` on
App Service, local app data elsewhere), one folder a user named by a hash of their id. Every change goes through
`MovieStudio.Edit` (save, then record for undo). A movie's id is its folder name (`MovieStudio.IsId`), and only
media types are served from inside it. A movie is the intro, the user's own films, and the outro: nothing generated goes between them. A script is
written with `EpisodeLength(films, films)` (so `BeatPlan.Repair` keeps only the film clips), edits that would add a
non-film clip are refused (`addClip`, `kind`, `removeVideo`), and `CheckLength` refuses a render with a clip that
has no film. Films come from the Library (staged from blob storage), so the Library's
1-minute limit applies; every clip must run under a minute and a movie up to 10 (`Durations.MaxClip`/`MaxMovie`,
checked in `MovieStudio.CheckLength` before anything is paid for). **Every render puts each film and AI video clip
through Meme-ify** (`MovieStudio.Memes`): the clip as it plays (voice-over mixed in) is uploaded as a hidden working
item, `VideoStep` runs on it directly (no run row, no daily quota) with the narration handed over as its speech, the
result is kept in the movie's `memes/` folder (named by everything that went into it, so an edit makes it again), and
the working blobs and output row are deleted. Kids' clips get Meme-ify without the roast, in the "sitcom" persona. Over a narrated clip the roast is a heckler: the
`VideoRoast.narrated` run option tells `RoastService` the speech is a narrator to answer (`NarratorBrief`), and
`TurnTaking.Arrange` moves a joke that would start mid-line to the end of that line. A clip can be left out of Meme-ify
(`Clip.NoMemes`, the "memes" edit). A movie render (or the one-go make) is one run of the daily quota
(`MovieStudio.StartRenderAsync`), refunded when the job ends without a video.
Its AI cost is logged as "meme-ify"; `Movie:MemeifyUsd` prices the estimate shown first. The finished video then goes
into the Library by itself, replacing the movie's earlier copy unless that was shared or pinned (`export/library.json`).
With no server speech model, a film that keeps its own sound is heard by the browser's Whisper
(`poDeviceAi.transcribeMedia`), stored as the item's source speech like an upload's. `SourceAudioAnalysis` uses speech
already stored with an item instead of transcribing it again. Spending is logged per movie (`SpendLog`) and held to an optional monthly budget; anything
priced asks first. The finished video can be put in the Library (`POST /api/movies/{id}/library`). YouTube upload
needs `YouTube:ClientId`/`YouTube:ClientSecret` (an OAuth client of the *Web application* kind, redirect
`/api/movies/youtube/callback`); the sign-in is used for that one upload and nothing is kept. Routes are under
`/api/movies` (`Features/Movies/MovieEndpoints`); the client is `Pages/CreateMovie` with one component per step
(`Shared/Movie*Step`), state in `Services/MovieStore`, and `wwwroot/js/movie.js` for undo keys and the preview's
live captions. Its advanced controls are the `po.adv.movie` switch.

**ffmpeg and fonts.** `Common/FFmpegProcess` finds ffmpeg via `FFmpegBinPath`, then `./ffmpeg/` beside the app, then
PATH; `Features/Render/FFmpegArgs` builds every command line as pure, unit-tested functions. App Service has no fonts,
so `scripts/package.sh` (run by `deploy.yml`) bundles a static ffmpeg and `fonts/DejaVuSans-Bold.ttf`. Linux uses SkiaSharp's NoDependencies
native build (no fontconfig), so the meme renderer falls back to that font file.

**Build version.** `Directory.Build.targets` stamps the client assembly (opt-in `StampBuildVersion`) with a `BuildStamp` (`YYMMDD` from the commit
date), `BuildCommit` (short hash) and `BuildTime` (UTC, to the minute); `MainLayout.razor` shows the stamp bottom-right
with the other two in its tooltip. MinVer separately sets the semantic version from Git tags, which is
why CI checks out with `fetch-depth: 0`.
