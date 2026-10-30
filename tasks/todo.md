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

## Open

- [ ] Run `scripts/package.sh` once on Linux/WSL, or watch the next deploy, to confirm the moved packaging steps.
- [ ] `scripts/run-e2e.ps1` stops when an Azurite container from another compose project already holds the name `poredomedia-azurite`.
