# To do

## Done: clean-up C1–C10 (2026-10-21)

- [x] C1 Repo hygiene: `.aura/` and `.vscode/mcp.json` untracked and ignored; screenshot set renamed to `261020-…`; this file and `DOCS/README.md` back.
- [x] C2 Vision: one `IVisionService` (Gemini, else Azure OpenAI) replaces the router and model picker; `ICombinedVisionAnalyzer` folded into `ISceneDetailProvider`.
- [x] C3 Dropped Ollama (`OllamaSharp`), the tags-only Azure Computer Vision provider, `AiProviderIds` and the `eval-ai` command.
- [x] C4 On-device AI behind Settings → Advanced; its JS in `js/local-ai/device-tasks.js`.
- [x] C5 Effects and interface sounds default off; depth preview and gallery shatter/fling removed.
- [x] C6 `/settings` page replaces the account drawer.
- [x] C7 Tried hiding Rap roast, Bulk styles, Photo to video and Insult roast behind a switch; reverted on request: every function always shows.
- [x] C8 Feed and Sounds tabs behind their switches; share links stay.
- [x] C9 Device-result endpoints split from `MediaEndpoints`; `VideoRunStep` → `VideoStep`.
- [x] C10 `scripts/package.sh` shared by `deploy.yml` and hand packaging; tests updated, budgets below the caps.

## Done: clean-up round 2, C1–C10 (2026-10-21)

- [x] C1 Dropped the unused on-device text model (WebLLM/Qwen) and its worker; one local model, Florence-2.
- [x] C2 Dropped Azure Computer Vision (`ComputerVision:*`, `Azure.AI.Vision.ImageAnalysis`); the rap roast reads the picture with its own vision call.
- [x] C3 Meme-ify vision: Gemini watches the whole clip, Foundry frames are the fallback; `Vision:WholeClip`, `Vision:FrameProvider` and the Gemini frame labeller removed.
- [x] C4 `Features/Memeify`, `VideoRoast` and `Captions` merged into `Features/Video`; vision and speech clients moved to `Common/Ai`.
- [x] C5 Cost chip left the header (the spend shows on Create and in Runs); Library page and Gallery merged into one page.
- [x] C6 Community feed and remix removed; share links stay.
- [x] C7 Sounds page, sound upload, favourites and spectrograms removed; Meme-ify keeps the seeded library.
- [x] C8 `package.py` folded into `package.sh`; seed-sounds looks in two places; Azurite pinned to 3.37.0.
- [x] C9 Exports under one route: `/api/media/{id}/export/{gif|srt|roast}`.
- [x] C10 Settings page unit tests. Sharing one test host between E2EAPI and Integration was skipped: it saves ~10 lines and couples two projects.

## Done: fewer functions (2026-10-21)

- [x] Upscale and Cutout removed everywhere (server, on-device cutout, Hugging Face, prices, config keys).
- [x] Rap roast has no options: always stand-up, Nuclear, strong language allowed.
- [x] Bulk styles: Clear button; empty prompt boxes are skipped and not listed.

## Done: server round, items 5–50 (2026-10-30)

- [x] 5 Social export: `POST/GET /api/media/{id}/export/social?aspect=9x16|1x1|16x9` (blur-fill, `FFmpegArgs.SocialArgs`), offered in the Library's More menu.
- [x] 31 Auto title and summary: `IMediaSummarizer` names each output (≤ 6 words + one sentence, stored as `Title`/`Summary`); Library search matches the summary; failures are logged and skipped.
- [x] 36 Telemetry: `StepTelemetryDto` (seconds, AI calls, tokens, cost) per step on the run and in progress events; shown per step in RunProgress (cost and tokens only with Cost estimates on).
- [x] 37 Provider fallback: Gemini ⇄ Azure OpenAI for vision and chat when both are configured; the run log says when it fell back.
- [x] 38 Roast quality check: `IRoastCritic` scores rap-roast lyrics and video roast jokes; under 6/10 is rewritten once with the critique.
- [x] 41 Retired storage dropped: the first housekeeping sweep per process deletes the `SoundFavorites` table and `media/*/script.json` blobs.
- [x] 43 `Features/Sounds` moved into `Features/Video` (namespace too).
- [x] 47 Unit tests folded from row-heavy theories into table-driven facts (same cases asserted): 97 → 80 with the new tests.
- [x] 48 Unused packages removed: `Radzen.Blazor` from the Api (the client ships it), `NSubstitute` from IntegrationTests, `coverlet.collector` everywhere. `OpenAI`, `Microsoft.Extensions.AI` and `Microsoft.Extensions.Caching.Hybrid` are used directly and stay.
- [x] 49 Dead endpoints: every mapped route has a caller in the client, tests or scripts; none removed.
- [x] 50 (server half) `GET/PUT /api/preferences` with `PreferencesDto`, antiforgery-protected, 16 KB cap, integration-tested.

## Done: UI/UX pass, items 21–50 (2026-10-30)

- [x] 21 Library: one toolbar (title, search, kind, sort, Add media); the bulk bar replaces it while items are ticked.
- [x] 22 A Library item opens in a slide-out drawer (`MediaDrawer`, a Radzen side dialog); Esc closes it; `?item=` deep links open it.
- [x] 23 Create in three tabbed steps (Pick media, Functions, Run), each fitting a laptop screen.
- [x] 24 Runs drawer as cards; run lists and run details have no sideways scroll at phone width.
- [x] 25 `PageBoundary` around every page body: Try again re-renders without a reload, then Reload; the layout boundary stays.
- [x] 26 `NavigationUiTests`: every nav route and header link, no console errors, layout shift under 0.1, plus `?` and `/`.
- [x] 27 Skeleton cards while the Library and the Runs drawer load.
- [x] 28 Empty states with a next step: the empty Library is a drop zone, Runs offers Go to Create, Create explains an empty library.
- [x] 29 Keys U, R, /, ? through one keydown listener (`ShortcutKeys`), disposed with the layout; `?` lists them.
- [x] 30 Build stamp tooltip: commit short hash and build time, stamped by `Directory.Build.targets`.
- [x] 35 Local first: with On-device AI on and Florence-2 cached, Create describes the picture on the device and falls back to the server; the run shows "On device" or "Server". The manual picker is gone; Settings has Get the model ready.
- [x] 42 Old recipes with Upscale or Cutout load with those steps skipped (`RecipeBook`, unit-tested).
- [x] 44 `Create.razor` and `Gallery.razor` split into `CreateSteps`, `RecipeBar`, `RunView`, `LibraryToolbar`, `LibraryGrid`, `MediaDrawer`.
- [x] 45 Shared is `IsAotCompatible`, the client `IsTrimmable` with the AOT analyzer: no warnings to fix (JSON was already source-generated).
- [x] 46 Disposal audit: a SignalR connection that failed to join, or that connected after the tracker was disposed, is now closed; new components release what they hold.
- [x] 50 (client half) Settings switches, effects, sound choices and recipes follow the account through `/api/preferences`.

## Done: Create Movie (2026-11-09)

- [x] PoAutoRobo's core copied in as `PoRedoMedia.Movie`: Azure/Windows clients removed, FFmpeg, fonts and the scene browser found on any OS.
- [x] A `General` show beside the Robot and Kids presets: shapes, prompts, kit, made-up camera logo and mascot.
- [x] Services wired from this app's providers (`Features/Movies/MovieAi`); stand-ins when one is missing.
- [x] `/api/movies` and `/movie`: Template, Films, Voice-over, Preview (advanced), Video; undo; spend and budget; YouTube by a web sign-in.
- [x] Tests: 6 unit, 1 integration, 2 browser; the nav test knows the new link.

## Done: Create Movie, round 2 (2026-11-09)

- [x] Clips under a minute, movies up to 10 minutes, checked before rendering and on the Films step.
- [x] Every film and AI video clip goes through Meme-ify at render (roast too, except in kids' videos); the movie lands in the Library.
- [x] Films that keep their sound are heard by the browser's speech model when the server has none; known speech is never transcribed twice.

## Done: Create Movie, round 3 (2026-11-09)

- [x] Meme-ify can be switched off for one clip (on by default).
- [x] The roast heckles the narrator: it is told the speech is a narrator to answer, and its lines wait for the narrator's pauses.
- [x] A movie render is one run of the daily quota, refunded when no video comes of it.

## Open

- [ ] The heckler roast is only tested with mock AI: try one narrated movie with the real roast and judge the jokes.

- [ ] Create Movie on the F1 plan: a render is minutes of CPU, and F1 allows 60 a day. Decide on a bigger plan, or keep movies to local runs.
- [ ] Movies, undo and the job live on one server (disk and memory). Mirror the folders to blob storage if the app ever runs on two instances.
- [ ] The browser's speech model (Whisper, a large first download) is not covered by a test: the films test runs with server speech.
- [ ] Meme-ify estimate is a flat price a clip (`Movie:MemeifyUsd`); the real cost is logged after, from the AI meter.
- [ ] YouTube upload: create the Web-application OAuth client and put `PoRedoMedia--YouTube--ClientId`/`--ClientSecret` in the vault.
- [ ] No timeline-strip drag or waveform on the Preview step (PoAutoRobo had both); clip reorder is by card drag and Ctrl+arrows.

- [ ] Run `scripts/package.sh` once on Linux/WSL, or watch the next deploy, to confirm the moved packaging steps.
- [ ] `scripts/run-e2e.ps1` stops when an Azurite container from another compose project already holds the name `poredomedia-azurite`.
