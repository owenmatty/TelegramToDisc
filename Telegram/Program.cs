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
    static readonly List<ChannelConfig> Channels = new()
    {
        new ChannelConfig { DisplayName = "FOOTBALL ON TV", DiscordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_FOOTBALL") },
        new ChannelConfig { DisplayName = "US SPORT ON TV", DiscordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_US") },
        new ChannelConfig { DisplayName = "COMBAT SPORT ON TV", DiscordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_COMBAT") },
        new ChannelConfig { DisplayName = "OTHER SPORT ON TV", DiscordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_OTHER") }
    };

    const string CacheFile = "Telegram/processed_cache.json";
    static List<CacheRecord> cacheRecords = new();
    static readonly HttpClient http = new();

    static async Task Main()
    {
        LoadCache();

        // Remove cache entries older than 3 days
        cacheRecords.RemoveAll(r => r.PostedAt < DateTime.UtcNow.AddDays(-3));

        // Write session from Base64 secret
        WriteSessionFromSecret();

        using var client = new WTelegram.Client(Config);
        await client.LoginUserIfNeeded();

        var dialogs = await client.Messages_GetAllDialogs();

        foreach (var channelConfig in Channels)
        {
            if (string.IsNullOrEmpty(channelConfig.DiscordWebhook)) continue;

            var channel = dialogs.chats.Values
                .OfType<Channel>()
                .FirstOrDefault(c => c.title.Contains(channelConfig.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (channel == null) continue;

            var inputPeer = channel.ToInputPeer();
            var history = await client.Messages_GetHistory(inputPeer, limit: 100);

            var messagesOrdered = history.Messages
                .OfType<Message>()
                .OrderBy(m => m.id);

            var ukZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
            var ukNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ukZone);

            foreach (var msg in messagesOrdered)
            {
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
                            await Task.Delay(2000); // avoid rate limits
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

        Directory.CreateDirectory("Telegram");
        File.WriteAllBytes("Telegram/session.session", Convert.FromBase64String(base64));
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
        try
        {
            Directory.CreateDirectory("Telegram");
            if (!File.Exists(CacheFile))
            {
                Console.WriteLine("Cache file not found, starting fresh.");
                cacheRecords = new();
                SaveCache(); // create file immediately
                return;
            }

            var json = File.ReadAllText(CacheFile);
            var dto = JsonSerializer.Deserialize<CacheDto>(json);
            cacheRecords = dto?.Records ?? new();
            Console.WriteLine($"Loaded {cacheRecords.Count} cached records.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load cache: {ex.Message}");
            cacheRecords = new();
        }
    }

    static void SaveCache()
    {
        try
        {
            Directory.CreateDirectory("Telegram");
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(new CacheDto { Records = cacheRecords }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save cache: {ex.Message}");
        }
    }

    class ChannelConfig { public string DisplayName; public string DiscordWebhook; }
    class CacheDto { public List<CacheRecord> Records { get; set; } = new(); }
    class CacheRecord { public string Key { get; set; } = ""; public DateTime PostedAt { get; set; } }

    static string Config(string what) => what switch
    {
        "api_id" => Environment.GetEnvironmentVariable("TELEGRAM_API_ID"),
        "api_hash" => Environment.GetEnvironmentVariable("TELEGRAM_API_HASH"),
        "phone_number" => Environment.GetEnvironmentVariable("TELEGRAM_PHONE"),
        "session_pathname" => "Telegram/session.session",
        _ => null
    };
}
