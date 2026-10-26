# Large video uploads

## Channel ID setup

If `Telegram.ChannelId` is missing or `0`, the uploader looks for channel-post updates received by the bot. Add the bot as an administrator of the target channel, publish a new post, and start the uploader. When exactly one channel is found, its ID is saved to the `appsettings.json` file beside the running app and uploads continue. The app needs write access to that file. Discovery waits up to 15 seconds for an update.

If the bot has a webhook, no pending channel post, or posts from multiple channels, set `ChannelId` manually. Discovery leaves pending updates unacknowledged so it does not consume updates belonging to another bot process. Telegram retains pending updates for a limited time; a fresh channel post is the simplest way to make the ID available.

By default, the uploader uses Telegram's public Bot API. Files above 49 MiB are compressed with FFmpeg before upload because the public API accepts files up to 50 MB.

To upload larger videos without recompression, run a [local Telegram Bot API server](https://github.com/tdlib/telegram-bot-api) and add its URL to the `Telegram` section of `appsettings.json`:

```json
{
  "Telegram": {
    "BotToken": "your-bot-token",
    "ChannelId": -1001234567890,
    "LocalBotApiUrl": "http://127.0.0.1:8081"
  }
}
```

Before switching the bot to the local server, call Telegram's [`logOut` method](https://core.telegram.org/bots/api#logout) on the public API once. The local server must be running and reachable from the machine running this uploader. With `LocalBotApiUrl` set, the uploader sends all videos through that server, keeps the original file, and accepts files up to 2,000 MB. Larger files are skipped with a warning. Leave the setting absent or empty to use the public API and its FFmpeg compression path.

The server URL is only the origin, such as `http://127.0.0.1:8081`; do not include `/bot<token>` in it. Use HTTPS or a trusted private network if the server is on another machine, since bot requests contain the token in the URL.

## FFmpeg on Linux

The Linux publish does not bundle FFmpeg. For public Bot API uploads larger than 49 MiB, install both `ffmpeg` and `ffprobe` on the Linux host. On Debian or Ubuntu:

```sh
sudo apt update
sudo apt install ffmpeg
ffmpeg -version
ffprobe -version
```

The account running the uploader must be able to execute both commands. Alternatively, place executable Linux binaries at `ffmpeg/bin/ffmpeg` and `ffmpeg/bin/ffprobe` beside the published `allgabar-web-tg-uploader` file. The app also checks for binaries directly beside itself, then falls back to `PATH`. A failed compression now prints the relevant FFmpeg error. If `LocalBotApiUrl` is configured, the app uploads without FFmpeg.
