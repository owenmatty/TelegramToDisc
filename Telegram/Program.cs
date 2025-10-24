using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TL;
using WTelegram;

class Program
{
    // List of private channels and their Discord webhooks
    static readonly List<ChannelConfig> Channels = new()
    {
        new ChannelConfig { DisplayName = "FOOTBALL ON TV", DiscordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_FOOTBALL") },
        new ChannelConfig { DisplayName = "US SPORT ON TV", DiscordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_BASKETBALL") },
        new ChannelConfig { DisplayName = "COMBAT SPORT ON TV", DiscordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_COMBAT") },
        new ChannelConfig { DisplayName = "OTHER SPORT ON TV", DiscordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_OTHER") }
    };

    const string CacheFile = "processed_cache.json";
    static List<CacheRecord> cacheRecords = new();
    static readonly HttpClient http = new();

    static async Task Main()
    {
        LoadCache();

        // Remove cache entries older than 3 days
        cacheRecords.RemoveAll(r => r.PostedAt < DateTime.UtcNow.AddDays(-3));

        // Write session from secret (if set)
        WriteSessionFromSecret();

        using var client = new WTelegram.Client(Config);
        await client.LoginUserIfNeeded();

        var dialogs = await client.Messages_GetAllDialogs();

        foreach (var channelConfig in Channels)
        {
            if (string.IsNullOrEmpty(channelConfig.DiscordWebhook))
            {
                Console.WriteLine($"Skipping {channelConfig.DisplayName}: no webhook set");
                continue;
            }

            // Find channel by display name
            var channel = dialogs.chats.Values
                            .OfType<Channel>()
                            .FirstOrDefault(c => c.title.Contains(channelConfig.DisplayName, StringComparison.OrdinalIgnoreCase));

            if (channel == null)
            {
                Console.WriteLine($"Channel '{channelConfig.DisplayName}' not found!");
                continue;
            }

            var inputPeer = channel.ToInputPeer();
            var history = await client.Messages_GetHistory(inputPeer, limit: 100);

            // Sort messages by oldest first
            var messagesOrdered = history.Messages
                .OfType<Message>()
                .OrderBy(m => m.id);

            // UK timezone for “today”
            var ukZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
            var ukNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ukZone);

            foreach (var msg in messagesOrdered)
            {
                // Skip if message was not posted today in UK time
                var msgUkDate = TimeZoneInfo.ConvertTimeFromUtc(msg.date, ukZone).Date;
                if (msgUkDate != ukNow.Date) continue;

                string key = $"{channelConfig.DisplayName}-{msg.id}";
                if (cacheRecords.Any(r => r.Key == key)) continue;

                if (msg.media is MessageMediaPhoto photoMedia && photoMedia.photo is Photo photo)
                {
                    try
                    {
                        using var ms = new MemoryStream();
                        await client.DownloadFileAsync(photo, ms);
                        byte[] fileBytes = ms.ToArray();

                        string filename = $"{channelConfig.DisplayName}_{msg.id}.jpg";
                        bool ok = await PostToDiscord(fileBytes, filename, msg.message, channelConfig.DiscordWebhook);

                        if (ok)
                        {
                            cacheRecords.Add(new CacheRecord { Key = key, PostedAt = DateTime.UtcNow });
                            SaveCache();
                            Console.WriteLine($"Posted photo from {channelConfig.DisplayName}, msg.id={msg.id}");

                            // Delay 2 seconds between posts to avoid rate limits
                            await Task.Delay(2000);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to process msg.id={msg.id}: {ex.Message}");
                    }
                }
            }
        }

        Console.WriteLine("Done.");
    }

    static void WriteSessionFromSecret()
    {
        var base64 = Environment.GetEnvironmentVariable("TELEGRAM_SESSION");
        if (string.IsNullOrEmpty(base64)) return;
        var bytes = Convert.FromBase64String(base64);
        File.WriteAllBytes("session.session", bytes);
    }

    static async Task<bool> PostToDiscord(byte[] fileData, string filename, string caption, string webhook)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            if (!string.IsNullOrEmpty(caption))
                content.Add(new StringContent(caption.Length <= 2000 ? caption : caption.Substring(0, 1990) + "..."), "content");

            content.Add(new ByteArrayContent(fileData), "file", filename);
            var resp = await http.PostAsync(webhook, content);
            resp.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to post to Discord: {ex.Message}");
            return false;
        }
    }

    static void LoadCache()
    {
        if (!File.Exists(CacheFile)) return;
        try
        {
            var json = File.ReadAllText(CacheFile);
            var dto = JsonSerializer.Deserialize<CacheDto>(json);
            if (dto?.Records != null)
                cacheRecords = dto.Records;
        }
        catch { }
    }

    static void SaveCache()
    {
        try
        {
            var dto = new CacheDto { Records = cacheRecords };
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    class ChannelConfig { public string DisplayName; public string DiscordWebhook; }
    class CacheDto { public List<CacheRecord> Records { get; set; } = new(); }
    class CacheRecord { public string Key { get; set; } = ""; public DateTime PostedAt { get; set; } }

    static string Config(string what)
    {
        return what switch
        {
            "api_id" => Environment.GetEnvironmentVariable("TELEGRAM_API_ID"),
            "api_hash" => Environment.GetEnvironmentVariable("TELEGRAM_A
