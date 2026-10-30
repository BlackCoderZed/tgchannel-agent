# HomieTV Telegram uploader

Configure `appsettings.json` using `appsettings.example.json` as a template. Add the bot as an administrator with posting rights in both Telegram channels.

If either channel ID is `0`, the app checks recent channel-post updates received by the bot. Publish a fresh post in each channel, then run the app. It matches channel titles to `MovieUploadChannelTitle` and `MoviePosterAndSynopsisChannelTitle` (case-insensitive), or matches `MovieUploadChannelUsername` if configured. When only one ID is missing and exactly one unassigned channel is present, it uses that channel. It saves discovered IDs in `appsettings.json`. If matching is ambiguous, it lists the channels it found and stops so movies cannot be sent to the wrong channel. A bot with an active webhook cannot use update-based discovery; set the IDs manually in that case.

Run with `dotnet run --project homietv-web-tg-uploader`. The app uses HomieTV's movie JSON endpoints for page listings, posters, synopses, and Megaup entries. It follows AdsFly's timed Skip flow and extracts Megaup's timed download link automatically. For a link-only check that does not upload a movie, run `dotnet run --project homietv-web-tg-uploader -- --resolve-only movie-slug`. If a link still cannot be resolved, a direct URL can be supplied in `ResolvedDownloadUrls`, keyed by movie slug. Movies with no Megaup entry are skipped. A download host may reject requests with HTTP 403; that server response is reported as an error.

For movie files over 49 MiB, configure `Telegram.LocalBotApiUrl` for a local Telegram Bot API server. Local uploads are capped at 2,000 MB. The app records successful uploads in `uploads.json` and retries poster posts that did not complete.
