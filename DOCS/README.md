# PoRedoMedia in one page

A private media studio. A signed-in user picks one image or one video, ticks the functions to
apply, and runs them as one job. Results land in the Library, where they can be downloaded, shared
by link, or used as the source of the next run. How the code is built is in
[CLAUDE.md](../CLAUDE.md); this page is what the app does.

## What a user sees by default

| Media | Functions |
|---|---|
| Image | Restyle, Meme caption, Rap roast, Photo to video, Bulk styles |
| Video | Meme-ify, Insult roast, Auto-captions |

The header has Create, Library (your media) and Runs; the account button opens **Settings**.
Create goes in three steps shown as tabs (Pick media, Functions, Run), each sized to fit a laptop screen. The
Library has one toolbar (search, kind, sort, Add media; it turns into the bulk actions while items are ticked), and
an item opens in a slide-out drawer with its details and exports (Esc closes it; `?item=` links still open it).
Empty pages say what to do next (an empty Library is itself the drop zone). Keys: **U** adds media, **R** runs,
**/** searches, **?** lists every shortcut. Hovering the build stamp (bottom right) shows its commit and build time.
Visual effects and interface sounds are off. A result can be shared by a private link (anyone with
the link can view it), and a video can be downloaded as a GIF, re-framed for social feeds (9:16, 1:1 or 16:9 on a
blurred fill; the first time uses a run), as its captions (SRT) or as its roast audio. Each result is given a short
AI-written title and a one-sentence summary after it is made, and Library search matches both. A run shows how long
each step took (and, with Cost estimates on, its tokens and estimated cost).
Meme-ify picks from a library of 300 popular meme sounds (`scripts/meme-sounds/sounds-metadata.json`, fetched from myinstants.com into storage by `SoundLibrarySeeder` at every start, which skips those already there; the `seed-sounds` command does the same by hand). The list is in popularity order: each sound's rank boosts its match and the 30 most famous are always on the AI director's menu, which is told to prefer famous sounds that fit. It has no page of its own.
Rap roast has no options: it is always a stand-up set at full strength with strong language allowed.
Bulk styles skips any empty prompt box; its Clear button empties all ten to type your own.

## Settings → Advanced

Every switch starts off. Each adds to the app and none is needed for it to work. They are kept in
the browser and, once signed in, follow the account: on sign-in the client reads `GET /api/preferences` (a per-user
string map, up to 16 KB) and the account's values win; after a change it sends the whole set back with
`PUT /api/preferences`. The switches, Visual effects, the interface-sound choices and saved recipes travel this way;
a signed-out visitor keeps them in the browser only.

| Switch | Adds |
|---|---|
| On-device AI | Describe pictures in the browser for free. **Get the model ready** in Settings downloads it once (about 230 MB); from then on Create uses it by itself and falls back to the server if it fails, and the run says which one looked |
| Recipes | Save a set of ticked functions and reuse it in one tap |
| Cost estimates | The estimated AI cost of a run, and the total for the tab (on Create and in Runs); tokens and cost per step |
| Visual effects | A fluid backdrop the pointer stirs (WebGPU; the glass backdrop elsewhere), particles, a run orb coloured by what the run is doing, burn-in picture reveals, confetti and sparks on download and share, a live waveform under playing tracks, tilting Library cards, and a buzz on phones when a run ends |

Under Look and sound, **Sound theme** picks the interface sounds: Studio (the original), Arcade or
Soft. A run that ends plays its own chime, one for done and one for failed.

## Configuration that decides what exists

A function is offered only when its provider is configured (see `Shared/ConfigKeys.cs`):
`Google:ApiKey` (Gemini images and vision, whole-clip video vision, Gemini Omni Flash for Photo to video, Lyria), `OpenAI:*` (chat;
vision when there is no Google key), `AiFoundry:*` (sampled-frame video vision, director, roast
voice, speech; `AiFoundry:ImageDeployment`, FLUX.1 Kontext, draws when Gemini images fail, e.g. out of credit; `AiFoundry:TtsDeployment` speaks the rap roast when Lyria fails). With both `Google:ApiKey` and `OpenAI:*`, a failed vision or chat call is retried once on the
other provider and the run log says so. Both roasts are scored by a critic and rewritten once when weak.
`Mocks:UseMockAi` (Test only) replaces every provider.

## Running it

See [AGENTS.md](../AGENTS.md#running-things) and the commands in [CLAUDE.md](../CLAUDE.md#commands).
