using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp;
using Telegram.Bot;
using Telegram.Bot.Types;
using File = System.IO.File;

namespace HomietvWebTgUploader;

public sealed class AppConfig
{
    public TelegramConfig Telegram { get; set; } = new();
    public int MaxPages { get; set; } = 2;
    public int MaxMovies { get; set; } = 40;
    public string ListingApiUrlTemplate { get; set; } = "https://www.homietv.com/api/movies?page={number}";
    // Movie slug -> direct, downloadable media URL. Useful when the site's ad gate needs human interaction.
    public Dictionary<string, string> ResolvedDownloadUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TelegramConfig
{
    public string BotToken { get; set; } = "";
    public long MovieUploadChannelId { get; set; }
    public long MoviePosterAndSynopsisChannelId { get; set; }
    public string MovieUploadChannelTitle { get; set; } = "Movie Upload";
    public string MoviePosterAndSynopsisChannelTitle { get; set; } = "Movie Poster and Synopsis";
    public string? MovieUploadChannelUsername { get; set; }
    public string? LocalBotApiUrl { get; set; }
}

public sealed class UploadRecord
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? MegaupUrl { get; set; }
    public long MovieMessageId { get; set; }
    public long? PosterMessageId { get; set; }
    public DateTime UploadedAtUtc { get; set; }
}

public sealed record Movie(string Slug, string Title, string? Poster, string? Synopsis, string? MegaupUrl);
public sealed record ResolvedMedia(string Url, string? Referer);

internal static class Program
{
    private const long PublicUploadLimit = 49L * 1024 * 1024;
    private const long LocalUploadLimit = 2_000_000_000L;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Missing configuration: {configPath}");
            return 1;
        }

        var config = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(configPath), JsonOptions);
        if (config is null || string.IsNullOrWhiteSpace(config.Telegram.BotToken)
            || config.MaxPages < 1 || config.MaxMovies < 1
            || !config.ListingApiUrlTemplate.Contains("{number}", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Set BotToken, positive limits, and a listing URL template containing {number}.");
            return 1;
        }

        using var siteHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        siteHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; HomietvWebTgUploader/1.0)");
        using var downloadHttp = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        downloadHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; HomietvWebTgUploader/1.0)");
        if (args.Length == 2 && args[0] == "--resolve-only")
        {
            var movie = await GetMovieAsync(siteHttp, args[1]);
            var source = config.ResolvedDownloadUrls.GetValueOrDefault(args[1]) ?? movie?.MegaupUrl;
            if (string.IsNullOrWhiteSpace(source))
            {
                Console.WriteLine($"[SKIP] {movie?.Title ?? args[1]}: HomieTV does not list a Megaup link.");
                return 1;
            }
            var media = await ResolveMediaUrlAsync(downloadHttp, source);
            if (media is null)
            {
                Console.WriteLine($"[SKIP] {movie?.Title ?? args[1]}: no downloadable file link found.");
                return 1;
            }
            Console.WriteLine($"[RESOLVED] {movie?.Title ?? args[1]}: {new Uri(media.Url).Host}");
            return 0;
        }
        var localApi = !string.IsNullOrWhiteSpace(config.Telegram.LocalBotApiUrl);
        using var telegramHttp = new HttpClient { Timeout = localApi ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(10) };
        var bot = new TelegramBotClient(new TelegramBotClientOptions(
            config.Telegram.BotToken, localApi ? config.Telegram.LocalBotApiUrl!.Trim() : null), telegramHttp);

        if (config.Telegram.MovieUploadChannelId == 0 || config.Telegram.MoviePosterAndSynopsisChannelId == 0)
        {
            try
            {
                if (!await DiscoverChannelIdsAsync(bot, config.Telegram)) return 1;
                await SaveChannelIdsAsync(configPath, config.Telegram);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CONFIG ERROR] Channel discovery failed: {ex.Message}");
                return 1;
            }
        }

        if (config.Telegram.MovieUploadChannelId == config.Telegram.MoviePosterAndSynopsisChannelId)
        {
            Console.Error.WriteLine("[CONFIG ERROR] The movie and poster channels must have different IDs.");
            return 1;
        }

        var historyPath = Path.Combine(AppContext.BaseDirectory, "uploads.json");
        var history = File.Exists(historyPath)
            ? JsonSerializer.Deserialize<List<UploadRecord>>(await File.ReadAllTextAsync(historyPath), JsonOptions) ?? []
            : new List<UploadRecord>();
        var bySlug = history.ToDictionary(x => x.Slug, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = 0;
        var uploadedThisRun = 0;
        var postersThisRun = 0;

        Console.WriteLine($"Movie listing        : {config.ListingApiUrlTemplate}");
        Console.WriteLine($"Max pages            : {config.MaxPages}");
        Console.WriteLine($"Max movies           : {config.MaxMovies}");
        Console.WriteLine($"Telegram API         : {(localApi ? "local (2000 MB upload limit)" : "public (50 MB upload limit)")}");
        Console.WriteLine($"Already uploaded     : {history.Count} movie(s)");
        Console.WriteLine($"Posters already sent : {history.Count(x => x.PosterMessageId is not null)}");
        Console.WriteLine(new string('─', 60));
        Console.WriteLine("═══ Scanning HomieTV movies ═══");

        for (var page = 1; page <= config.MaxPages && found < config.MaxMovies; page++)
        {
            JsonObject? listing;
            var url = config.ListingApiUrlTemplate.Replace("{number}", page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine($"    [PAGE {page}] {url}");
            try
            {
                listing = JsonNode.Parse(await siteHttp.GetStringAsync(url)) as JsonObject;
                if (listing?["success"]?.GetValue<bool>() != true || listing["data"] is not JsonArray)
                    throw new InvalidDataException("Listing API response has no movie array.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PAGE {page}] {ex.Message}");
                break;
            }

            var items = (JsonArray)listing["data"]!;
            var newCount = items.Count(item => ReadString(item, "slug") is { } slug && !bySlug.ContainsKey(slug));
            Console.WriteLine($"    [PAGE {page}] Found {items.Count} movie(s), {newCount} not yet uploaded");
            if (items.Count == 0)
            {
                Console.WriteLine("    [PAGE] No more movies.");
                break;
            }
            foreach (var item in items)
            {
                var slug = ReadString(item, "slug");
                if (string.IsNullOrWhiteSpace(slug) || !seen.Add(slug)) continue;
                if (found >= config.MaxMovies) break;
                found++;

                try
                {
                    if (bySlug.TryGetValue(slug, out var existing) && existing.PosterMessageId is not null)
                    {
                        Console.WriteLine($"  [SKIP] Already uploaded: {existing.Title}");
                        continue;
                    }

                    Console.WriteLine($"  [SCAN] {ReadString(item, "title") ?? slug}");
                    Console.WriteLine($"         https://www.homietv.com/movie/{slug}");
                    var movie = await GetMovieAsync(siteHttp, slug);
                    if (movie is null)
                    {
                        Console.WriteLine($"[SKIP] {slug}: no details");
                        continue;
                    }

                    if (existing is null)
                    {
                        var mediaUrl = config.ResolvedDownloadUrls.GetValueOrDefault(slug) ?? movie.MegaupUrl;
                        if (string.IsNullOrWhiteSpace(mediaUrl))
                        {
                            Console.WriteLine("  [WARN] HomieTV does not list a Megaup link. Skipping.");
                            continue;
                        }

                        Console.WriteLine("  [RESOLVE] Finding Megaup download link …");
                        var media = await ResolveMediaUrlAsync(downloadHttp, mediaUrl);
                        if (media is null)
                        {
                            Console.WriteLine($"  [WARN] No downloadable file found. Set ResolvedDownloadUrls[\"{slug}\"] if you have a direct URL.");
                            continue;
                        }
                        Console.WriteLine($"  [RESOLVE] Download host: {new Uri(media.Url).Host}");

                        var tempFile = Path.Combine(Path.GetTempPath(), $"homietv_{Guid.NewGuid():N}.mp4");
                        try
                        {
                            Console.WriteLine($"  [DOWNLOAD] {movie.Title}");
                            await DownloadAsync(downloadHttp, media, tempFile);
                            var size = new FileInfo(tempFile).Length;
                            var limit = localApi ? LocalUploadLimit : PublicUploadLimit;
                            if (size > limit)
                            {
                                Console.WriteLine($"  [WARN] {size / 1048576d:F1} MiB exceeds the {(localApi ? "local" : "public")} Bot API limit. Configure a local Bot API server for large movies.");
                                continue;
                            }

                            Console.WriteLine("  [UPLOAD] Sending movie to Telegram …");
                            await using var stream = File.OpenRead(tempFile);
                            var filename = SafeFilename(movie.Title) + ".mp4";
                            var sent = await bot.SendVideoAsync(
                                chatId: config.Telegram.MovieUploadChannelId,
                                video: InputFile.FromStream(stream, filename),
                                caption: Trim(movie.Title, 1024),
                                supportsStreaming: true);
                            existing = new UploadRecord
                            {
                                Slug = slug,
                                Title = movie.Title,
                                MegaupUrl = movie.MegaupUrl,
                                MovieMessageId = sent.MessageId,
                                UploadedAtUtc = DateTime.UtcNow
                            };
                            history.Add(existing);
                            bySlug.Add(slug, existing);
                            await SaveHistoryAsync(historyPath, history);
                            uploadedThisRun++;
                            Console.WriteLine($"  [UPLOAD] ✓ Telegram MessageId: {sent.MessageId}");
                        }
                        finally
                        {
                            if (File.Exists(tempFile)) File.Delete(tempFile);
                        }
                    }

                    if (existing.PosterMessageId is null)
                    {
                        if (string.IsNullOrWhiteSpace(movie.Poster))
                        {
                            Console.WriteLine("  [WARN] No poster URL. Poster post remains pending.");
                            continue;
                        }

                        Console.WriteLine("  [POSTER] Sending poster and synopsis to Telegram …");
                        var movieLink = BuildMessageLink(config.Telegram, existing.MovieMessageId);
                        var heading = $"{movie.Title}\n\nMovie: {movieLink}";
                        var synopsis = movie.Synopsis?.Trim() ?? "";
                        var caption = Trim(heading + (synopsis.Length > 0 ? "\n\n" + synopsis : ""), 1024);
                        var poster = await bot.SendPhotoAsync(
                            chatId: config.Telegram.MoviePosterAndSynopsisChannelId,
                            photo: InputFile.FromUri(movie.Poster),
                            caption: caption);
                        existing.PosterMessageId = poster.MessageId;
                        await SaveHistoryAsync(historyPath, history);
                        postersThisRun++;
                        Console.WriteLine($"  [POSTER] ✓ Telegram MessageId: {poster.MessageId}");

                        // Preserve the full synopsis when Telegram's photo caption limit is reached.
                        if (caption.Length < heading.Length + 2 + synopsis.Length && synopsis.Length > 0)
                        {
                            var remaining = synopsis[Math.Max(0, caption.Length - heading.Length - 2)..].Trim();
                            foreach (var chunk in SplitText(remaining, 4000))
                                await bot.SendTextMessageAsync(
                                    chatId: config.Telegram.MoviePosterAndSynopsisChannelId,
                                    text: chunk,
                                    replyToMessageId: poster.MessageId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [ERROR] {slug}: {ex.Message}");
                }
            }

            var lastPage = listing["meta"]?["last_page"]?.GetValue<int>() ?? page;
            if (page >= lastPage) break;
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 60));
        Console.WriteLine($"Done. Scanned {found} movie(s); uploaded {uploadedThisRun}; posted {postersThisRun} poster(s).");
        Console.WriteLine($"Total uploaded movies: {history.Count}; posters sent: {history.Count(x => x.PosterMessageId is not null)}");
        Console.WriteLine($"Upload history saved to: {historyPath}");
        return 0;
    }

    private static async Task<bool> DiscoverChannelIdsAsync(TelegramBotClient bot, TelegramConfig config)
    {
        Console.WriteLine("[CONFIG] Looking for channel-post updates to discover missing channel IDs.");
        var webhook = await bot.GetWebhookInfoAsync();
        if (!string.IsNullOrEmpty(webhook.Url))
        {
            Console.Error.WriteLine("[CONFIG ERROR] The bot uses a webhook. Set the channel IDs manually, or disable the webhook before discovery.");
            return false;
        }

        // Reading without an offset leaves updates available to other bot processes.
        var updates = await bot.GetUpdatesAsync(limit: 100, timeout: 15);
        var channels = updates.Where(update => update.ChannelPost is not null)
            .Select(update => update.ChannelPost!.Chat)
            .GroupBy(chat => chat.Id)
            .Select(group => group.First())
            .ToList();
        if (channels.Count == 0)
        {
            Console.Error.WriteLine("[CONFIG ERROR] No channel posts found. Add the bot as an admin, post once in each channel, and restart.");
            return false;
        }

        var reserved = new HashSet<long>();
        if (config.MovieUploadChannelId != 0) reserved.Add(config.MovieUploadChannelId);
        if (config.MoviePosterAndSynopsisChannelId != 0) reserved.Add(config.MoviePosterAndSynopsisChannelId);

        long? Match(string title, string? username)
        {
            var matches = channels.Where(chat => !reserved.Contains(chat.Id)
                && ((title.Length > 0 && string.Equals(chat.Title?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(username)
                        && string.Equals(chat.Username, username.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase))))
                .ToList();
            return matches.Count == 1 ? matches[0].Id : null;
        }

        if (config.MovieUploadChannelId == 0)
        {
            var id = Match(config.MovieUploadChannelTitle ?? "", config.MovieUploadChannelUsername);
            if (id is not null)
            {
                config.MovieUploadChannelId = id.Value;
                reserved.Add(id.Value);
            }
        }
        if (config.MoviePosterAndSynopsisChannelId == 0)
        {
            var id = Match(config.MoviePosterAndSynopsisChannelTitle ?? "", null);
            if (id is not null)
            {
                config.MoviePosterAndSynopsisChannelId = id.Value;
                reserved.Add(id.Value);
            }
        }

        // A single unassigned channel is unambiguous when only one ID remains missing.
        if ((config.MovieUploadChannelId == 0) != (config.MoviePosterAndSynopsisChannelId == 0))
        {
            var remaining = channels.Where(chat => !reserved.Contains(chat.Id)).ToList();
            if (remaining.Count == 1)
            {
                if (config.MovieUploadChannelId == 0) config.MovieUploadChannelId = remaining[0].Id;
                else config.MoviePosterAndSynopsisChannelId = remaining[0].Id;
            }
        }

        if (config.MovieUploadChannelId == 0 || config.MoviePosterAndSynopsisChannelId == 0)
        {
            Console.Error.WriteLine("[CONFIG ERROR] Could not safely identify both channels. Set channel IDs or exact channel titles in appsettings.json. Available channel posts:");
            foreach (var channel in channels)
                Console.Error.WriteLine($"  {channel.Id}: {channel.Title}");
            return false;
        }

        Console.WriteLine($"[CONFIG] Movie channel: {config.MovieUploadChannelId}; poster channel: {config.MoviePosterAndSynopsisChannelId}");
        return true;
    }

    private static async Task SaveChannelIdsAsync(string configPath, TelegramConfig config)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(configPath)) as JsonObject
            ?? throw new InvalidDataException("Configuration must be a JSON object.");
        var telegram = root.FirstOrDefault(pair => string.Equals(pair.Key, "Telegram", StringComparison.OrdinalIgnoreCase)).Value as JsonObject
            ?? throw new InvalidDataException("Telegram configuration is missing.");
        SetId("MovieUploadChannelId", config.MovieUploadChannelId);
        SetId("MoviePosterAndSynopsisChannelId", config.MoviePosterAndSynopsisChannelId);
        await File.WriteAllTextAsync(configPath, root.ToJsonString(JsonOptions));
        Console.WriteLine($"[CONFIG] Saved discovered channel IDs to {configPath}");

        void SetId(string name, long value)
        {
            var key = telegram.Select(pair => pair.Key)
                .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) ?? name;
            telegram[key] = value;
        }
    }

    private static async Task<Movie?> GetMovieAsync(HttpClient http, string slug)
    {
        var url = "https://www.homietv.com/api/movies/" + Uri.EscapeDataString(slug);
        var root = JsonNode.Parse(await http.GetStringAsync(url));
        var data = root?["data"];
        if (data is null) return null;
        string? megaup = null;
        if (data["movie_download_links"] is JsonArray links)
        {
            // Prefer the smallest advertised Megaup copy, which is more likely to fit Telegram's limit.
            megaup = links
                .Where(x => string.Equals(ReadString(x, "server_name")?.Trim(), "Megaup", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => ParseSize(ReadString(x, "size")))
                .Select(x => ReadString(x, "url"))
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }
        return new Movie(slug, ReadString(data, "title") ?? slug,
            ReadString(data, "poster"), ReadString(data, "overview"), megaup);
    }

    private static async Task<ResolvedMedia?> ResolveMediaUrlAsync(HttpClient http, string initialUrl)
    {
        if (!Uri.TryCreate(initialUrl, UriKind.Absolute, out var current) || !IsHttp(current)) return null;
        Uri? referer = null;
        for (var hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Referrer = referer;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var finalUrl = response.RequestMessage?.RequestUri ?? current;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || type == "application/octet-stream"
                || response.Content.Headers.ContentDisposition?.FileName is not null)
                return new ResolvedMedia(finalUrl.ToString(), referer?.ToString());
            if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;

            var html = await response.Content.ReadAsStringAsync();
            if (IsHost(finalUrl, "adsfly.org"))
            {
                var destination = await ResolveAdsFlyAsync(http, finalUrl, html);
                if (destination is null) return null;
                referer = finalUrl;
                current = destination;
                continue;
            }

            if (IsHost(finalUrl, "megaup.net"))
            {
                var destination = ExtractMegaupDownloadUrl(html);
                if (destination is null || !IsHost(destination, "megaup.net")) return null;
                // The generated link is temporary. Download it with the Megaup page as Referer.
                return new ResolvedMedia(destination.ToString(), finalUrl.ToString());
            }

            if (html.Contains("captcha", StringComparison.OrdinalIgnoreCase)) return null;
            var context = BrowsingContext.New(Configuration.Default);
            var document = await context.OpenAsync(req => req.Content(html).Address(finalUrl.ToString()));
            var candidate = document.QuerySelector("a[download][href], a#download[href], a.download[href]")?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            referer = finalUrl;
            current = new Uri(finalUrl, candidate);
            if (!IsHttp(current)) return null;
        }
        return null;
    }

    private static async Task<Uri?> ResolveAdsFlyAsync(HttpClient http, Uri pageUrl, string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html).Address(pageUrl.ToString()));
        var pageData = document.QuerySelector("#app[data-page]")?.GetAttribute("data-page");
        var csrf = document.QuerySelector("meta[name='csrf-token']")?.GetAttribute("content");
        if (string.IsNullOrWhiteSpace(pageData) || string.IsNullOrWhiteSpace(csrf)) return null;
        var props = JsonNode.Parse(pageData)?["props"];
        var encoded = ReadString(props, "e");
        var accessToken = ReadString(props, "access_token");
        if (string.IsNullOrWhiteSpace(encoded) || string.IsNullOrWhiteSpace(accessToken)) return null;

        // AdsFly's normal Skip button waits five seconds before making this request.
        await Task.Delay(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(pageUrl, "/get-real-url"));
        request.Headers.Referrer = pageUrl;
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { e = encoded, access_token = accessToken }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var destination = ReadString(JsonNode.Parse(await response.Content.ReadAsStringAsync()), "url");
        return Uri.TryCreate(destination, UriKind.Absolute, out var uri) && IsHttp(uri) ? uri : null;
    }

    private static Uri? ExtractMegaupDownloadUrl(string html)
    {
        var start = html.IndexOf("function display()", StringComparison.Ordinal);
        if (start < 0) return null;
        var end = html.IndexOf("function triggerFreeDownload", start, StringComparison.Ordinal);
        var fragment = html[start..(end > start ? end : Math.Min(html.Length, start + 4000))];
        var match = Regex.Match(fragment, "href='(?<url>https?://[^']+)'", RegexOptions.IgnoreCase);
        var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsHttp(uri) ? uri : null;
    }

    private static async Task DownloadAsync(HttpClient http, ResolvedMedia media, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, media.Url);
        if (Uri.TryCreate(media.Referer, UriKind.Absolute, out var referer))
            request.Headers.Referrer = referer;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Download returned a web page, not a movie file.");
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        var totalBytes = response.Content.Headers.ContentLength;
        var elapsed = Stopwatch.StartNew();
        var lastProgress = TimeSpan.Zero;
        var progressShown = false;
        long downloaded = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            downloaded += read;
            if (elapsed.Elapsed - lastProgress < TimeSpan.FromSeconds(1)) continue;
            var amount = $"{downloaded / 1048576d:F1} MiB";
            var percent = totalBytes is > 0 ? $"{downloaded * 100d / totalBytes.Value:F1}% (" + amount + ")" : amount;
            Console.Write($"\r  [DOWNLOAD] {percent}   ");
            progressShown = true;
            lastProgress = elapsed.Elapsed;
        }
        if (progressShown) Console.WriteLine();
        if (target.Length == 0) throw new InvalidDataException("Download is empty.");
        Console.WriteLine($"  [DOWNLOAD] Complete – {downloaded / 1048576d:F1} MiB");
    }

    private static async Task SaveHistoryAsync(string path, List<UploadRecord> history)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(history, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static string BuildMessageLink(TelegramConfig config, long messageId)
    {
        if (!string.IsNullOrWhiteSpace(config.MovieUploadChannelUsername))
            return $"https://t.me/{config.MovieUploadChannelUsername.Trim().TrimStart('@')}/{messageId}";
        var id = config.MovieUploadChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (id.StartsWith("-100", StringComparison.Ordinal))
            return $"https://t.me/c/{id[4..]}/{messageId}";
        throw new InvalidOperationException("Set MovieUploadChannelUsername or use a -100... channel ID to create a message link.");
    }

    private static string? ReadString(JsonNode? node, string key) => node?[key]?.ToString();
    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    private static bool IsHost(Uri uri, string host) =>
        uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    private static string Trim(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
    private static string SafeFilename(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return Trim(clean.Length == 0 ? "movie" : clean, 120);
    }

    private static double ParseSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return double.MaxValue;
        var match = System.Text.RegularExpressions.Regex.Match(size, @"([\d.]+)\s*(GB|MB)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number)) return double.MaxValue;
        return number * (match.Groups[2].Value.Equals("GB", StringComparison.OrdinalIgnoreCase) ? 1024 : 1);
    }

    private static IEnumerable<string> SplitText(string value, int max)
    {
        for (var offset = 0; offset < value.Length; offset += max)
            yield return value.Substring(offset, Math.Min(max, value.Length - offset));
    }
}
