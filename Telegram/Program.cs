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
        new ChannelConfig { DisplayName = "FOOTBALL ON TV", DiscordWebhook = "https://discord.com/api/webhooks/1431014691471102075/87a65H33MdHUpb3DPx1FLkirtGdGGiSRmC4YfJ-d-0LaHSvPfLrIKRNjI8YDzr8unle3" },
        new ChannelConfig { DisplayName = "US SPORT ON TV", DiscordWebhook = "https://discord.com/api/webhooks/1431018332785610885/AFk1IHRYGH1lmni4UuPeNpBlqnFj10P8_uxomsPdQ0BcWqdZRhIR2LFgJAvy17IQFyL5" },
        new ChannelConfig { DisplayName = "COMBAT SPORT ON TV", DiscordWebhook = "https://discord.com/api/webhooks/1431178792071593994/e3dwuqC8uxZgs6_7vMQ_-o6EJAUjFTd3JDQFLmW84nhC-PN8vrPBInoIaIeqjMkvYpfV" },
         new ChannelConfig { DisplayName = "OTHER SPORT ON TV", DiscordWebhook = "https://discord.com/api/webhooks/1431179108510994464/m06RbroqM-se3vODHLbTdwUV1-btbDKe2nsayqvP19a3C0UX4HKERIc0-05R8u2a0npc" }
        // Add more channels here
    };

    const string CacheFile = "processed_cache.json";
    static List<CacheRecord> cacheRecords = new(); // store as key + timestamp
    static readonly HttpClient http = new();

    static async Task Main()
    {
        LoadCache();

        // Remove cache entries older than 3 days
        cacheRecords.RemoveAll(r => r.PostedAt < DateTime.UtcNow.AddDays(-3));

        using var client = new WTelegram.Client(Config);
        await client.LoginUserIfNeeded();

        // Fetch all dialogs once
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
                .OrderBy(m => m.id); // oldest first

            foreach (var msg in messagesOrdered)
            {
                string key = $"{channelConfig.DisplayName}-{msg.id}";
                if (cacheRecords.Any(r => r.Key == key)) continue;

                if (msg.media is MessageMediaPhoto photoMedia && photoMedia.photo is Photo photo)
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
            }
        }

        Console.WriteLine("Done.");
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
            "api_id" => "23361435",
            "api_hash" => "67fea5638553ea1d1a0c169944a36580",
            "phone_number" => "+447850257756",
            "session_pathname" => "session.session",
            _ => null
        };
    }
}
