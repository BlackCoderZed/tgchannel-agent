// buumal-web-tg-uploader - Scrapes configured websites for video URLs and uploads them to Telegram.
//
// Configuration (appsettings.json):
//   "Telegram": { "BotToken": "...", "ChannelId": -1001234567890 }
//   "Websites": [ "https://example.com/" ]
//
// The app:
//   1. Visits each website's listing page(s) with pagination support.
//   2. For each post, extracts the actual video download URL (download button or embedded player).
//   3. Downloads the video file.
//   4. Uploads it to the configured Telegram channel.
//   5. Records every uploaded video in uploads.json to avoid duplicates.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Telegram.Bot;

using File = System.IO.File;
using InputFile = Telegram.Bot.Types.InputFile;

namespace BuumalWebTgUploader;

// ──── Configuration models ────

public class AppConfig
{
    public TelegramConfig Telegram { get; set; } = new();
    public string[] Websites { get; set; } = Array.Empty<string>();
    public int MaxVideosPerSite { get; set; } = 50;
    public int MaxPages { get; set; } = 5;
}

public class TelegramConfig
{
    public string BotToken { get; set; } = string.Empty;
    public long ChannelId { get; set; }
    public string? LocalBotApiUrl { get; set; }
}

// ──── Upload record (persisted in uploads.json) ────

public class UploadRecord
{
    public string PostUrl { get; set; } = string.Empty;
    public string VideoUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long TelegramMessageId { get; set; }
    public DateTime UploadedAt { get; set; }
}

// ──── Video scraper ────

public static class VideoScraper
{
    /// <summary>
    /// Scrapes listing pages (with pagination) to find individual post/video page URLs.
    /// </summary>
    public static async Task<List<(string PostUrl, string Title)>> GetPostLinksAsync(
        HttpClient http, string siteUrl, int maxVideos, int maxPages)
    {
        var results = new List<(string PostUrl, string Title)>();
        var currentUrl = siteUrl;

        for (int page = 1; page <= maxPages; page++)
        {
            Console.WriteLine($"    [PAGE {page}] {currentUrl}");

            string html;
            try
            {
                html = await http.GetStringAsync(currentUrl);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"    [ERROR] Could not fetch page {page}: {ex.GetBaseException().Message}");
                Program.PrintWebsiteConnectionHint(ex);
                break;
            }

            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var doc = await context.OpenAsync(req => req.Content(html).Address(currentUrl));

            int countBefore = results.Count;

            // Buu Mal post links use /video/{id}, including on query-string pages.
            foreach (var link in doc.QuerySelectorAll("a[href]"))
            {
                var href = ResolveHttpUrl(currentUrl, link.GetAttribute("href"));
                if (href == null
                    || !NormalizeHost(new Uri(href).Host).Equals(NormalizeHost(new Uri(siteUrl).Host), StringComparison.OrdinalIgnoreCase)
                    || !new Uri(href).AbsolutePath.StartsWith("/video/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var title = CleanTitle(link.QuerySelector("h1, h2, h3, h4, h5, h6")?.TextContent)
                    ?? CleanTitle(link.TextContent)
                    ?? CleanTitle(link.GetAttribute("title"))
                    ?? CleanTitle(link.QuerySelector("img[alt]")?.GetAttribute("alt"));
                if (!results.Any(r => r.PostUrl.Equals(href, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add((href, title ?? new Uri(href).Segments.Last()));
                    if (results.Count >= maxVideos) break;
                }
            }

            Console.WriteLine($"    [PAGE {page}] Found {results.Count - countBefore} new post(s) (total: {results.Count})");

            if (results.Count >= maxVideos)
                break;

            // Find next page link
            var nextPageUrl = FindNextPageUrl(doc, currentUrl);
            if (nextPageUrl == null || nextPageUrl.Equals(currentUrl, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("    [PAGE] No more pages.");
                break;
            }

            currentUrl = nextPageUrl;
        }

        return results;
    }

    /// <summary>
    /// Finds the "next page" URL from pagination links.
    /// </summary>
    private static string? FindNextPageUrl(IDocument doc, string currentUrl)
    {
        var nextLink = doc.QuerySelector("a[rel='next'][href], a.next[href]");
        if (nextLink != null)
        {
            var href = ResolveHttpUrl(currentUrl, nextLink.GetAttribute("href"));
            if (href != null) return href;
        }

        // The site's pager uses a plain "Next" link to ?page=N.
        foreach (var link in doc.QuerySelectorAll("a[href]"))
        {
            var text = link.TextContent.Trim();
            if (text is "›" or "»" or "Next" or "next" or "Next »" or "→")
            {
                var href = ResolveHttpUrl(currentUrl, link.GetAttribute("href"));
                if (href != null) return href;
            }
        }

        return null;
    }

    private static string? ResolveHttpUrl(string baseUrl, string? href)
    {
        if (string.IsNullOrWhiteSpace(href)
            || !Uri.TryCreate(new Uri(baseUrl), href.Trim(), out var url)
            || url.Scheme is not ("http" or "https"))
            return null;

        return url.AbsoluteUri;
    }

    private static string NormalizeHost(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static string? CleanTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var title = Regex.Replace(text, @"\s+", " ").Trim();
        title = Regex.Replace(title,
            @"\s*\d+\s+(?:seconds?|minutes?|hours?|days?|weeks?|months?|years?)\s+ago$",
            "", RegexOptions.IgnoreCase).Trim();
        return title.Length == 0 ? null : title;
    }

    /// <summary>
    /// Visits an individual post page and extracts its title and direct video download URL.
    /// Priority order:
    ///   1. Download button
    ///   2. Direct video/source tags and video metadata
    ///   3. iframe with base64-encoded q= parameter containing a source src
    ///   4. Any .mp4 URL in the HTML
    /// </summary>
    public static async Task<(string? DownloadUrl, string? Title)> GetVideoPageAsync(HttpClient http, string postUrl)
    {
        var html = await http.GetStringAsync(postUrl);
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var doc = await context.OpenAsync(req => req.Content(html).Address(postUrl));
        var pageTitle = CleanTitle(doc.QuerySelector("h5")?.TextContent)
            ?? CleanTitle(doc.QuerySelector("main h1, main h2, main h3, main h4, article h1, article h2, article h3, article h4")?.TextContent);

        (string? DownloadUrl, string? Title) Result(string? url) => (url, pageTitle);

        // Strategy 1 (preferred): Download button link
        foreach (var link in doc.QuerySelectorAll("a[href]"))
        {
            var href = ResolveHttpUrl(postUrl, link.GetAttribute("href"));
            if (href == null) continue;

            var label = $"{link.Id} {link.ClassName} {link.TextContent}";
            if (link.HasAttribute("download")
                || label.Contains("download", StringComparison.OrdinalIgnoreCase))
                return Result(href);
        }

        // Direct video elements and metadata can point to a file without an .mp4 suffix.
        foreach (var element in doc.QuerySelectorAll("video[src], video[data-src], source[src], source[data-src], meta[property='og:video'][content], meta[property='og:video:url'][content]"))
        {
            var url = ResolveHttpUrl(postUrl,
                element.GetAttribute("src") ?? element.GetAttribute("data-src") ?? element.GetAttribute("content"));
            if (url != null && !new Uri(url).AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                return Result(url);
        }

        // Some players encode their source markup in an iframe URL.
        var iframes = doc.QuerySelectorAll("iframe[src]");
        foreach (var iframe in iframes)
        {
            var src = iframe.GetAttribute("src") ?? "";
            var match = Regex.Match(src, @"[?&]q=([A-Za-z0-9+/=]+)");
            if (match.Success)
            {
                try
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(
                        Convert.FromBase64String(match.Groups[1].Value));
                    var urlDecoded = WebUtility.UrlDecode(decoded);

                    var mp4Match = Regex.Match(urlDecoded,
                        @"(?:src|href)\s*=\s*[""']?(https?://[^""'\s>]+\.mp4(?:\?[^""'\s>]+)?)[""'\s>]?",
                        RegexOptions.IgnoreCase);
                    if (mp4Match.Success)
                        return Result(WebUtility.HtmlDecode(mp4Match.Groups[1].Value));
                }
                catch (FormatException) { }
            }
        }

        // Inline player configuration often escapes slashes and includes a signed query string.
        var unescapedHtml = WebUtility.HtmlDecode(html.Replace("\\/", "/"));
        var mp4Regex = new Regex(@"https?://[^\s""'<>\\]+\.mp4(?:\?[^\s""'<>\\]+)?", RegexOptions.IgnoreCase);
        var mp4InHtml = mp4Regex.Match(unescapedHtml);
        if (mp4InHtml.Success)
            return Result(mp4InHtml.Value);

        return Result(null);
    }

}

// ──── Main program ────

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Load configuration
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Configuration file not found: {configPath}");
            return 1;
        }

        var configJson = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize<AppConfig>(configJson,
                         new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? throw new InvalidOperationException("Invalid configuration");

        if (args.Contains("--check-site", StringComparer.OrdinalIgnoreCase))
        {
            var siteOverride = args.SkipWhile(arg => !arg.Equals("--check-site", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
            var sitesToCheck = siteOverride == null ? config.Websites : new[] { siteOverride };
            using var probe = CreateWebsiteHttpClient();
            probe.Timeout = TimeSpan.FromSeconds(30);
            foreach (var site in sitesToCheck)
            {
                try
                {
                    using var response = await probe.GetAsync(site);
                    Console.WriteLine($"{site}: {(int)response.StatusCode} {response.RequestMessage?.RequestUri}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"{site}: {ex}");
                    PrintWebsiteConnectionHint(ex);
                    return 1;
                }
            }
            return 0;
        }

        if (string.IsNullOrWhiteSpace(config.Telegram.BotToken))
        {
            Console.Error.WriteLine("Telegram BotToken not configured in appsettings.json.");
            return 1;
        }

        if (config.Websites.Length == 0)
        {
            Console.Error.WriteLine("No websites configured in appsettings.json.");
            return 1;
        }

        // A self-hosted Bot API server accepts videos up to 2000 MB without recompression.
        var localBotApiUrl = config.Telegram.LocalBotApiUrl?.Trim();
        var useLocalBotApi = !string.IsNullOrWhiteSpace(localBotApiUrl);
        var telegramHttpClient = new HttpClient
        {
            Timeout = useLocalBotApi ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(10)
        };
        var botClient = new TelegramBotClient(
            new TelegramBotClientOptions(config.Telegram.BotToken, useLocalBotApi ? localBotApiUrl : null),
            telegramHttpClient);

        if (config.Telegram.ChannelId == 0)
        {
            try
            {
                var channelId = await DiscoverChannelIdAsync(botClient);
                if (channelId == null) return 1;

                await SaveChannelIdAsync(configPath, channelId.Value);
                config.Telegram.ChannelId = channelId.Value;
                Console.WriteLine($"[CONFIG] Saved ChannelId {channelId.Value} to {configPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CONFIG ERROR] Could not discover or save ChannelId: {ex.Message}");
                return 1;
            }
        }

        using var http = CreateWebsiteHttpClient();

        // Load upload history
        var uploadsPath = Path.Combine(AppContext.BaseDirectory, "uploads.json");
        var uploads = File.Exists(uploadsPath)
            ? JsonSerializer.Deserialize<List<UploadRecord>>(await File.ReadAllTextAsync(uploadsPath))
              ?? new List<UploadRecord>()
            : new List<UploadRecord>();

        var uploadedPostUrls = uploads.Select(u => u.PostUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"Configured websites : {config.Websites.Length}");
        Console.WriteLine($"Max pages per site  : {config.MaxPages}");
        Console.WriteLine($"Max videos per site : {config.MaxVideosPerSite}");
        Console.WriteLine($"Telegram API        : {(useLocalBotApi ? "local (2000 MB upload limit)" : "public (50 MB upload limit)")}");
        Console.WriteLine($"Already uploaded     : {uploads.Count} video(s)");
        Console.WriteLine(new string('─', 60));

        foreach (var siteUrl in config.Websites)
        {
            Console.WriteLine();
            Console.WriteLine($"═══ Scanning: {siteUrl} ═══");

            List<(string PostUrl, string Title)> posts;
            try
            {
                posts = await VideoScraper.GetPostLinksAsync(http, siteUrl, config.MaxVideosPerSite, config.MaxPages);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] Could not fetch listing from {siteUrl}: {ex.Message}");
                continue;
            }

            Console.WriteLine($"Total posts found: {posts.Count}");
            Console.WriteLine();

            foreach (var (postUrl, title) in posts)
            {
                if (uploadedPostUrls.Contains(postUrl))
                {
                    Console.WriteLine($"  [SKIP] Already uploaded: {title}");
                    continue;
                }

                Console.WriteLine($"  [SCAN] {title}");
                Console.WriteLine($"         {postUrl}");

                (string? DownloadUrl, string? Title) videoPage;
                try
                {
                    videoPage = await VideoScraper.GetVideoPageAsync(http, postUrl);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [ERROR] Could not scrape post: {ex.Message}");
                    continue;
                }

                var videoUrl = videoPage.DownloadUrl;
                var resolvedTitle = videoPage.Title ?? title;
                if (!resolvedTitle.Equals(title, StringComparison.Ordinal))
                    Console.WriteLine($"  [TITLE] {resolvedTitle}");

                if (string.IsNullOrWhiteSpace(videoUrl))
                {
                    Console.WriteLine("  [WARN] No video URL found on this page. Skipping.");
                    continue;
                }

                Console.WriteLine($"  [DOWNLOAD] {videoUrl}");
                var tempFile = Path.Combine(Path.GetTempPath(), $"vtu_{Guid.NewGuid()}.mp4");
                string? compressedFile = null;
                try
                {
                    // Build request with Referer header (some download servers require it)
                    var downloadRequest = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                    downloadRequest.Headers.Referrer = new Uri(postUrl);
                    using var response = await http.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength;

                    await using (var contentStream = await response.Content.ReadAsStreamAsync())
                    {
                        await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

                        var buffer = new byte[81920];
                        long totalRead = 0;
                        int bytesRead;
                        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                            totalRead += bytesRead;
                            if (totalBytes.HasValue && totalBytes > 0)
                            {
                                var pct = (double)totalRead / totalBytes.Value * 100;
                                Console.Write($"\r  [DOWNLOAD] {pct:F1}% ({totalRead / 1048576.0:F1} MB)   ");
                            }
                        }
                        Console.WriteLine();
                        Console.WriteLine($"  [DOWNLOAD] Complete – {totalRead / 1048576.0:F1} MB");

                    } // fileStream and contentStream are closed here

                    // Public Bot API uploads are limited to 50 MB; leave a small margin.
                    // The local Bot API server permits up to 2000 MB without quality loss.
                    var fileSize = new FileInfo(tempFile).Length;
                    var fileSizeMB = fileSize / 1048576.0;
                    var uploadFile = tempFile;

                    if (useLocalBotApi && fileSize > 2_000_000_000L)
                    {
                        Console.WriteLine($"  [WARN] File is {fileSizeMB:F1} MiB, above the local Bot API 2000 MB limit. Skipping.");
                        continue;
                    }

                    if (!useLocalBotApi && fileSize > 49L * 1048576)
                    {
                        Console.WriteLine($"  [COMPRESS] File is {fileSizeMB:F1} MB – compressing with FFmpeg …");
                        compressedFile = Path.Combine(Path.GetTempPath(), $"vtu_{Guid.NewGuid()}_compressed.mp4");
                        var compressed = await CompressVideoAsync(tempFile, compressedFile, 49);
                        if (compressed)
                        {
                            var compressedBytes = new FileInfo(compressedFile).Length;
                            var compressedSize = compressedBytes / 1048576.0;
                            Console.WriteLine($"  [COMPRESS] Done – {compressedSize:F1} MB");
                            if (compressedBytes > 49L * 1048576)
                            {
                                Console.WriteLine("  [WARN] Compressed video still exceeds the public Bot API upload limit. Skipping.");
                                continue;
                            }
                            uploadFile = compressedFile;
                        }
                        else
                        {
                            Console.WriteLine("  [WARN] FFmpeg compression failed. Skipping this video.");
                            if (File.Exists(compressedFile)) File.Delete(compressedFile);
                            continue;
                        }
                    }

                    // Upload to Telegram with extended timeout
                    Console.WriteLine("  [UPLOAD] Sending to Telegram …");
                    await using var uploadStream = File.OpenRead(uploadFile);
                    var fileName = Path.GetFileName(new Uri(videoUrl).AbsolutePath);
                    if (string.IsNullOrWhiteSpace(fileName)) fileName = "video.mp4";
                    var inputFile = InputFile.FromStream(uploadStream, fileName);

                    var caption = string.IsNullOrWhiteSpace(resolvedTitle) ? postUrl : resolvedTitle;
                    // Telegram caption limit is 1024 chars
                    if (caption.Length > 1024) caption = caption[..1021] + "…";

                    var sentMessage = await botClient.SendVideoAsync(
                        chatId: config.Telegram.ChannelId,
                        video: inputFile,
                        caption: caption);

                    var record = new UploadRecord
                    {
                        PostUrl = postUrl,
                        VideoUrl = videoUrl,
                        Title = resolvedTitle,
                        TelegramMessageId = sentMessage.MessageId,
                        UploadedAt = DateTime.UtcNow
                    };
                    uploads.Add(record);
                    uploadedPostUrls.Add(postUrl);

                    Console.WriteLine($"  [UPLOAD] ✓ Telegram MessageId: {sentMessage.MessageId}");

                    // Save after each successful upload
                    await File.WriteAllTextAsync(uploadsPath,
                        JsonSerializer.Serialize(uploads, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [ERROR] {ex.Message}");
                }
                finally
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                    if (compressedFile != null && File.Exists(compressedFile))
                        File.Delete(compressedFile);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 60));
        Console.WriteLine($"Done. Total uploaded videos: {uploads.Count}");
        Console.WriteLine($"Upload history saved to: {uploadsPath}");
        return 0;
    }

    private static HttpClient CreateWebsiteHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        return client;
    }

    internal static void PrintWebsiteConnectionHint(Exception ex)
    {
        if (ex.GetBaseException() is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            Console.Error.WriteLine("    [NETWORK] The connection was reset before page data arrived. Check that the device VPN is connected and routes this website.");
    }

    static async Task<long?> DiscoverChannelIdAsync(TelegramBotClient botClient)
    {
        Console.WriteLine("[CONFIG] ChannelId is missing. Checking channel posts received by the bot …");

        var webhook = await botClient.GetWebhookInfoAsync();
        if (!string.IsNullOrEmpty(webhook.Url))
        {
            Console.Error.WriteLine("[CONFIG ERROR] The bot uses a webhook, so getUpdates cannot discover a channel. Set ChannelId manually in appsettings.json.");
            return null;
        }

        // No offset: inspecting updates must not acknowledge or discard other bot updates.
        var updates = await botClient.GetUpdatesAsync(limit: 100, timeout: 15);
        var channels = updates
            .Where(update => update.ChannelPost != null)
            .Select(update => update.ChannelPost!.Chat)
            .GroupBy(chat => chat.Id)
            .Select(group => group.First())
            .ToList();

        if (channels.Count == 1)
            return channels[0].Id;

        if (channels.Count == 0)
            Console.Error.WriteLine("[CONFIG ERROR] No channel post found. Add the bot as a channel admin, publish a post, then restart. Or set ChannelId manually.");
        else
        {
            Console.Error.WriteLine("[CONFIG ERROR] Posts from multiple channels were found. Set ChannelId manually to one of these:");
            foreach (var channel in channels)
                Console.Error.WriteLine($"  {channel.Id}: {channel.Title}");
        }

        return null;
    }

    static async Task SaveChannelIdAsync(string configPath, long channelId)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(configPath))
            ?? throw new InvalidOperationException("Configuration is empty.");
        var rootObject = root as JsonObject
            ?? throw new InvalidOperationException("Configuration must be a JSON object.");
        var telegram = rootObject
            .FirstOrDefault(pair => string.Equals(pair.Key, "Telegram", StringComparison.OrdinalIgnoreCase))
            .Value as JsonObject
            ?? throw new InvalidOperationException("Telegram configuration is missing.");
        var channelIdKey = telegram
            .Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, "ChannelId", StringComparison.OrdinalIgnoreCase))
            ?? "ChannelId";
        telegram[channelIdKey] = channelId;

        // Write the existing file so its permissions remain unchanged (it contains the bot token).
        await File.WriteAllTextAsync(configPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Compresses a video file using FFmpeg to fit within the target size in MB.
    /// </summary>
    static async Task<bool> CompressVideoAsync(string inputPath, string outputPath, double targetSizeMB)
    {
        try
        {
            var ffprobe = GetFfmpegExecutable("ffprobe");
            var ffmpeg = GetFfmpegExecutable("ffmpeg");
            var probeAvailable = await IsAvailableAsync(ffprobe);
            var ffmpegAvailable = await IsAvailableAsync(ffmpeg);
            if (!probeAvailable || !ffmpegAvailable)
            {
                Console.Error.WriteLine("  [COMPRESS ERROR] FFmpeg and ffprobe are required. Install them on Linux or place both executables in the published app's ffmpeg/bin directory.");
                return false;
            }

            // Get video duration first
            using var durationProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v error -show_entries format=duration -of csv=p=0 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            durationProcess.Start();
            var durationOutput = await durationProcess.StandardOutput.ReadToEndAsync();
            var durationError = durationProcess.StandardError.ReadToEndAsync();
            await durationProcess.WaitForExitAsync();
            if (durationProcess.ExitCode != 0)
            {
                Console.Error.WriteLine($"  [COMPRESS ERROR] ffprobe exited with code {durationProcess.ExitCode}: { (await durationError).Trim() }");
                return false;
            }
            await durationError;

            if (!double.TryParse(durationOutput.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var durationSeconds) || durationSeconds <= 0)
            {
                durationSeconds = 300; // fallback: assume 5 min
            }

            // Calculate target bitrate (in kbps): targetSize(kB) * 8 / duration(s) * 0.95 safety margin
            var targetBitrateKbps = (int)(targetSizeMB * 1024 * 8 / durationSeconds * 0.90);
            // Reserve ~128kbps for audio
            var videoBitrateKbps = Math.Max(200, targetBitrateKbps - 128);
            var audioBitrateKbps = 128;

            Console.WriteLine($"  [COMPRESS] Duration: {durationSeconds:F0}s, Target video bitrate: {videoBitrateKbps} kbps");

            using var ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{inputPath}\" -c:v libx264 -b:v {videoBitrateKbps}k -c:a aac -b:a {audioBitrateKbps}k -preset fast -movflags +faststart \"{outputPath}\"",
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            ffmpegProcess.Start();

            // Read stderr (FFmpeg outputs progress to stderr)
            var recentErrors = new Queue<string>();
            var stderrTask = Task.Run(async () =>
            {
                using var reader = ffmpegProcess.StandardError;
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Contains("frame=") || line.Contains("time="))
                        Console.Write($"\r  [COMPRESS] {line.Trim()[..Math.Min(line.Trim().Length, 80)]}   ");
                    else if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (recentErrors.Count == 8) recentErrors.Dequeue();
                        recentErrors.Enqueue(line);
                    }
                }
            });

            await ffmpegProcess.WaitForExitAsync();
            await stderrTask;
            Console.WriteLine();

            if (ffmpegProcess.ExitCode != 0)
                Console.Error.WriteLine($"  [COMPRESS ERROR] ffmpeg exited with code {ffmpegProcess.ExitCode}: {string.Join(" | ", recentErrors)}");

            return ffmpegProcess.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [COMPRESS ERROR] {ex.Message}");
            return false;
        }
    }

    static string GetFfmpegExecutable(string name)
    {
        var executableName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", executableName);
        if (File.Exists(bundledPath)) return bundledPath;

        var besideAppPath = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(besideAppPath)) return besideAppPath;

        return executableName; // Resolve from PATH.
    }

    static async Task<bool> IsAvailableAsync(string executable)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(output, error, process.WaitForExitAsync());
            if (process.ExitCode == 0) return true;
            Console.Error.WriteLine($"  [COMPRESS ERROR] {executable} exited with code {process.ExitCode}: {error.Result.Trim()}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [COMPRESS ERROR] Cannot run {executable}: {ex.Message}");
        }
        return false;
    }
}

