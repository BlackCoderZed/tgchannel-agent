// VideoTelegramUploader - Scrapes configured websites for video URLs and uploads them to Telegram.
//
// Configuration (appsettings.json):
//   "Telegram": { "BotToken": "...", "ChannelId": -1001234567890 }
//   "Websites": [ "https://allgabar.com/" ]
//
// The app:
//   1. Visits each website's listing page(s) with pagination support.
//   2. For each post, extracts the actual video download URL (download button or embedded player).
//   3. Downloads the video file.
//   4. Uploads it to the configured Telegram channel.
//   5. Records every uploaded video in uploads.json to avoid duplicates.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using Telegram.Bot;

using File = System.IO.File;
using InputFile = Telegram.Bot.Types.InputFile;

namespace VideoTelegramUploader;

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
                Console.Error.WriteLine($"    [ERROR] Could not fetch page {page}: {ex.Message}");
                break;
            }

            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var doc = await context.OpenAsync(req => req.Content(html).Address(currentUrl));

            int countBefore = results.Count;

            // Strategy 1: <article> elements containing an <a> with href
            var articles = doc.QuerySelectorAll("article.thumb-block a[href], article.loop-video a[href]");
            foreach (var link in articles)
            {
                var href = link.GetAttribute("href") ?? "";
                var title = link.GetAttribute("title") ?? link.TextContent.Trim();
                if (!string.IsNullOrWhiteSpace(href)
                    && !string.IsNullOrWhiteSpace(title)
                    && !results.Any(r => r.PostUrl == href))
                {
                    results.Add((href, title));
                    if (results.Count >= maxVideos) break;
                }
            }

            // Strategy 2 (fallback): any <a> inside a .videos-list container
            if (countBefore == results.Count)
            {
                var videoLinks = doc.QuerySelectorAll(".videos-list a[href], .video-list a[href], .post-listing a[href]");
                foreach (var link in videoLinks)
                {
                    var href = link.GetAttribute("href") ?? "";
                    var title = link.GetAttribute("title") ?? link.TextContent.Trim();
                    if (!string.IsNullOrWhiteSpace(href)
                        && href.Contains("/archives/", StringComparison.OrdinalIgnoreCase)
                        && !results.Any(r => r.PostUrl == href))
                    {
                        results.Add((href, title));
                        if (results.Count >= maxVideos) break;
                    }
                }
            }

            Console.WriteLine($"    [PAGE {page}] Found {results.Count - countBefore} new post(s) (total: {results.Count})");

            if (results.Count >= maxVideos)
                break;

            // Find next page link
            var nextPageUrl = FindNextPageUrl(doc, currentUrl);
            if (nextPageUrl == null)
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
        // Look for a "next" pagination link
        // Common patterns: <a class="next" href="...">, <a rel="next">, <a> with "›" or "Next" text
        var nextLink = doc.QuerySelector("a.next[href], a[rel='next'][href], .pagination a.next[href]");
        if (nextLink != null)
        {
            var href = nextLink.GetAttribute("href");
            if (!string.IsNullOrWhiteSpace(href))
                return href;
        }

        // Look for pagination links with "›" or "»" or "Next" text
        var paginationLinks = doc.QuerySelectorAll(".pagination a[href], .nav-links a[href], .page-numbers a[href]");
        foreach (var link in paginationLinks)
        {
            var text = link.TextContent.Trim();
            if (text is "›" or "»" or "Next" or "next" or "Next »" or "→")
            {
                var href = link.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href))
                    return href;
            }
        }

        // Fallback: try to construct next page URL from the current URL pattern
        // e.g., /page/1/ -> /page/2/
        var pageMatch = Regex.Match(currentUrl, @"/page/(\d+)/?");
        if (pageMatch.Success)
        {
            int currentPage = int.Parse(pageMatch.Groups[1].Value);
            return Regex.Replace(currentUrl, @"/page/\d+/?", $"/page/{currentPage + 1}/");
        }

        // If the current URL has no /page/ and we found posts, try /page/2/
        var baseUri = new Uri(currentUrl);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        return $"{baseUri.Scheme}://{baseUri.Host}{basePath}/page/2/";
    }

    /// <summary>
    /// Visits an individual post page and extracts the direct .mp4 video download URL.
    /// Priority order:
    ///   1. Download button (a#tracking-url or a with "download" text and .mp4 href)
    ///   2. iframe with base64-encoded q= parameter containing a source src
    ///   3. Direct video/source tags
    ///   4. Any .mp4 URL in the HTML
    /// </summary>
    public static async Task<string?> GetVideoDownloadUrlAsync(HttpClient http, string postUrl)
    {
        var html = await http.GetStringAsync(postUrl);
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var doc = await context.OpenAsync(req => req.Content(html).Address(postUrl));

        // Strategy 1 (preferred): Download button link
        var downloadLink = doc.QuerySelector("a#tracking-url[href]");
        if (downloadLink != null)
        {
            var href = downloadLink.GetAttribute("href") ?? "";
            if (!string.IsNullOrWhiteSpace(href))
                return href;
        }

        // Also look for any <a> with "download" in its text that links to .mp4
        var allLinks = doc.QuerySelectorAll("a[href]");
        foreach (var link in allLinks)
        {
            var href = link.GetAttribute("href") ?? "";
            var text = link.TextContent.Trim();
            var cssClass = link.GetAttribute("class") ?? "";
            if (href.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                && (text.Contains("download", StringComparison.OrdinalIgnoreCase)
                    || cssClass.Contains("download", StringComparison.OrdinalIgnoreCase)))
            {
                return href;
            }
        }

        // Strategy 2: iframe with base64-encoded q= parameter (clean-tube-player style)
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
                        @"(?:src|href)\s*=\s*[""']?(https?://[^""'\s>]+\.mp4)[""'\s>]?",
                        RegexOptions.IgnoreCase);
                    if (mp4Match.Success)
                        return mp4Match.Groups[1].Value;
                }
                catch (FormatException) { }
            }
        }

        // Strategy 3: <video><source src="...mp4"> directly in the page
        var sources = doc.QuerySelectorAll("video source[src], source[type='video/mp4']");
        foreach (var source in sources)
        {
            var src = source.GetAttribute("src") ?? "";
            if (src.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return src;
        }

        // Strategy 4: any mp4 URL in the HTML
        var mp4Regex = new Regex(@"https?://[^\s""'<>]+\.mp4", RegexOptions.IgnoreCase);
        var mp4InHtml = mp4Regex.Match(html);
        if (mp4InHtml.Success)
            return mp4InHtml.Value;

        return null;
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

        if (string.IsNullOrWhiteSpace(config.Telegram.BotToken) || config.Telegram.ChannelId == 0)
        {
            Console.Error.WriteLine("Telegram BotToken or ChannelId not configured in appsettings.json.");
            return 1;
        }

        if (config.Websites.Length == 0)
        {
            Console.Error.WriteLine("No websites configured in appsettings.json.");
            return 1;
        }

        // Use a long timeout HttpClient for Telegram uploads (10 minutes)
        var telegramHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var botClient = new TelegramBotClient(config.Telegram.BotToken, telegramHttpClient);

        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

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

                string? videoUrl;
                try
                {
                    videoUrl = await VideoScraper.GetVideoDownloadUrlAsync(http, postUrl);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [ERROR] Could not scrape post: {ex.Message}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(videoUrl))
                {
                    Console.WriteLine("  [WARN] No video URL found on this page. Skipping.");
                    continue;
                }

                Console.WriteLine($"  [DOWNLOAD] {videoUrl}");
                var tempFile = Path.Combine(Path.GetTempPath(), $"vtu_{Guid.NewGuid()}.mp4");
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

                    // Check file size – compress if over 49 MB (Telegram limit is 50 MB)
                    var fileSize = new FileInfo(tempFile).Length;
                    var fileSizeMB = fileSize / 1048576.0;
                    var uploadFile = tempFile;
                    string? compressedFile = null;

                    if (fileSizeMB > 49)
                    {
                        Console.WriteLine($"  [COMPRESS] File is {fileSizeMB:F1} MB – compressing with FFmpeg …");
                        compressedFile = Path.Combine(Path.GetTempPath(), $"vtu_{Guid.NewGuid()}_compressed.mp4");
                        var compressed = await CompressVideoAsync(tempFile, compressedFile, 49);
                        if (compressed)
                        {
                            var compressedSize = new FileInfo(compressedFile).Length / 1048576.0;
                            Console.WriteLine($"  [COMPRESS] Done – {compressedSize:F1} MB");
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

                    var caption = string.IsNullOrWhiteSpace(title) ? postUrl : title;
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
                        Title = title,
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
                    // Clean up any compressed temp file
                    foreach (var f in Directory.GetFiles(Path.GetTempPath(), "vtu_*_compressed.mp4"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 60));
        Console.WriteLine($"Done. Total uploaded videos: {uploads.Count}");
        Console.WriteLine($"Upload history saved to: {uploadsPath}");
        return 0;
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
            if (!await IsAvailableAsync(ffprobe) || !await IsAvailableAsync(ffmpeg))
            {
                Console.Error.WriteLine("  [COMPRESS ERROR] FFmpeg and ffprobe are required. On Linux, install FFmpeg and ensure both commands are on PATH.");
                return false;
            }

            // Get video duration first
            var durationProcess = new Process
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
            await durationProcess.WaitForExitAsync();

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

            var ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{inputPath}\" -c:v libx264 -b:v {videoBitrateKbps}k -c:a aac -b:a {audioBitrateKbps}k -preset fast -movflags +faststart \"{outputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            ffmpegProcess.Start();

            // Read stderr (FFmpeg outputs progress to stderr)
            _ = Task.Run(async () =>
            {
                using var reader = ffmpegProcess.StandardError;
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Contains("frame=") || line.Contains("time="))
                        Console.Write($"\r  [COMPRESS] {line.Trim()[..Math.Min(line.Trim().Length, 80)]}   ");
                }
            });

            await ffmpegProcess.WaitForExitAsync();
            Console.WriteLine();

            return ffmpegProcess.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [COMPRESS ERROR] {ex.Message}");
            return false;
        }
    }

    static string GetFfmpegExecutable(string name) => OperatingSystem.IsWindows()
        ? Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", name + ".exe")
        : name;

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

