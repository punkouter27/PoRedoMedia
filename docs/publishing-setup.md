# Publishing to YouTube and TikTok: setup checklist

Create Movie's **Publish** button sends a finished movie to YouTube (the video, its thumbnail, title, description,
tags and category, "made for kids" for the Kids series) and the upright 9:16 copy to TikTok (as a draft in your TikTok
inbox, or as a direct post once TikTok allows it). Each site appears only when its app keys are set; until then the
Publish dialog says what is missing. You connect each account once from the dialog; the server keeps the connection
encrypted (ASP.NET Core Data Protection) in the `PlatformTokens` table, per signed-in user, and **Disconnect** revokes it
and deletes it. Nothing of the movie stays on the server after it has been published (or downloaded).

The redirect URIs below use the production host `https://<your-app>.azurewebsites.net` and local
`http://localhost:4100`; use your real host names.

## YouTube (Google Cloud)

1. Go to <https://console.cloud.google.com/> and create a project (for example "PoRedoMedia publishing").
2. **APIs & Services → Library**: enable **YouTube Data API v3**.
3. **APIs & Services → OAuth consent screen**:
   - User type **External**; app name, support email, developer email.
   - Scopes: add `https://www.googleapis.com/auth/youtube.upload` (the only one the app asks for).
   - While the app is in **Testing**, add your own Google account under **Test users**. Testing-mode consent may
     expire after a while; if Publish says to connect again, press Connect YouTube again.
4. **APIs & Services → Credentials → Create credentials → OAuth client ID**, type **Web application**. Authorized
   redirect URIs:
   - `https://<your-app>.azurewebsites.net/api/publish/youtube/callback`
   - `http://localhost:4100/api/publish/youtube/callback` (local runs)
   - Keep the existing `/api/movies/youtube/callback` entries if you use the older per-upload "Upload to YouTube"
     form too.
5. Put the client id and secret in Key Vault `kv-poshared2` (never in a committed file):
   - `PoRedoMedia--YouTube--ClientId`
   - `PoRedoMedia--YouTube--ClientSecret`
6. Restart the app, open a rendered movie, press **Publish → Connect YouTube**, and sign in.

Notes:

- Quota: each upload costs from the project's YouTube quota (the default allows a limited number of uploads a day).
  When it runs out, Publish says to try tomorrow. You can ask Google for more quota in the console.
- Custom thumbnails work only on a channel whose phone number is verified (YouTube Studio → Settings → Channel →
  Feature eligibility). Without it the video still uploads; Publish says the thumbnail was not set.
- If YouTube keeps a video private when you asked for public or unlisted (projects Google has not audited may be held
  to private), Publish says so; change it in YouTube Studio, or apply for an audit of the API project.

## TikTok (TikTok for Developers)

1. Go to <https://developers.tiktok.com/>, sign in, and **Manage apps → Connect an app** (create an app).
2. Add the products **Login Kit** and **Content Posting API**.
3. Login Kit → **Redirect URI** (web): `https://<your-app>.azurewebsites.net/api/publish/tiktok/callback`. TikTok
   accepts only https redirect URIs, so connecting TikTok works from the deployed app, not from `http://localhost`.
4. Scopes: `user.info.basic` and `video.upload` (send to inbox as a draft). Add `video.publish` only if you want
   direct posts.
5. Put the keys in Key Vault `kv-poshared2`:
   - `PoRedoMedia--TikTok--ClientKey`
   - `PoRedoMedia--TikTok--ClientSecret`
   - `PoRedoMedia--TikTok--DirectPost` = `true` only after `video.publish` is approved (otherwise leave it out).
6. Submit the app for **review**. Until TikTok audits it:
   - drafts to your inbox work for the accounts you add as testers (sandbox / target users);
   - direct posts may only be **Only me** (SELF_ONLY), and only to a private account. The dialog says so.
7. Restart the app, open a rendered movie, press **Publish → Connect TikTok**, and sign in.

After a draft arrives, open TikTok, check your inbox notification, paste the caption (also in the download package's
`tiktok.txt`), and post it.

## Where it lives in the code

- `src/PoRedoMedia.Api/Features/Publishing/`: the two site clients behind `IYouTubePublisher` / `ITikTokPublisher`,
  the encrypted `PlatformTokenStore`, and the connect / callback / disconnect endpoints. In the Test environment (mock AI)
  both are replaced by `FakePublishers`, so nothing reaches Google or TikTok.
- `src/PoRedoMedia.Api/Features/Movies/MovieStudio.Publishing.cs`: the Publish job.
- `src/PoRedoMedia.Client/Features/Movies/PublishDialog.razor`: the dialog, checked by
  `src/PoRedoMedia.Shared/Validation/PublishRequestValidator.cs` (which the server runs too).
