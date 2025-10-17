using System.Text.Json;
using System.Text.Json.Serialization;

namespace JoshHeaps.Net.Services.Implementations;

public class AutoIpUpdateService(
        IConfiguration config,
        ILogger<AutoIpUpdateService> log)
    : BackgroundService
{
    public static bool IsEnabled { get; private set; } = false;

    private static readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);
    private static readonly HttpClient _httpClient = new();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        IsEnabled = true;
        var timer = new PeriodicTimer(_checkInterval);

        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                await UpdateIpAddressIfChanged();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Error while attempting ip update");
            }
        }
    }

    private async Task UpdateIpAddressIfChanged()
    {
        var dnsRecords = await GetDnsRecordAsync();
        string dnsRecordIp = dnsRecords[0].Content;

        if (!dnsRecords.Records.All(x => x.Content == dnsRecords[0].Content))
            dnsRecordIp = string.Empty;

        string publicIp = await GetPublicIpAsync() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(publicIp) || dnsRecordIp == publicIp)
            return;

        foreach (var dnsRecord in dnsRecords.Records)
        {
            if (dnsRecord.Content == publicIp)
                continue;

            await UpdateDnsIpAsync(config, dnsRecord, publicIp);
        }
    }

    private static async Task<string> GetPublicIpAsync()
    {
        try
        {
            return await _httpClient.GetStringAsync(@"https://api.ipify.org/");
        }
        catch
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("Reattempting to grab public ip");

            return await GetPublicIpAsync();
        }
    }

    private async Task<RecordList> GetDnsRecordAsync()
    {
        try
        {
            HttpClient cfClient = new();
            cfClient.DefaultRequestHeaders.Add("X-Auth-Email", config["cfEmail"]);
            cfClient.DefaultRequestHeaders.Add("X-Auth-Key", config["cfKey"]);
            var result = await cfClient.GetAsync(@$"https://api.cloudflare.com/client/v4/zones/{config["zoneId"]}/dns_records");
            Console.WriteLine(await result.Content.ReadAsStringAsync());
            var records = JsonSerializer.Deserialize<RecordList>(await result.Content.ReadAsStringAsync());
            return records!;
        }
        catch
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("Reattempting to grab current ip");

            return await GetDnsRecordAsync();
        }
    }

    private static async Task UpdateDnsIpAsync(IConfiguration config, DnsRecord record, string ip)
    {
        HttpClient cfClient = new();
        cfClient.DefaultRequestHeaders.Add("X-Auth-Email", config["cfEmail"]);
        cfClient.DefaultRequestHeaders.Add("X-Auth-Key", config["cfKey"]);

        object updateRequestBody = new
        {
            name = record.Name,
            ttl = record.Ttl,
            type = record.Type,
            comment = record.Comment,
            content = ip,
            proxied = record.Proxied,
        };

        var result = await cfClient.PatchAsJsonAsync(@$"https://api.cloudflare.com/client/v4/zones/{config["zoneId"]}/dns_records/{record.Id}", updateRequestBody);

        if (!result.IsSuccessStatusCode)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("Reattempting to update ip");

            await UpdateDnsIpAsync(config, record, ip);
        }
    }

    record DnsRecord(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("ttl")] int Ttl,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("comment")] string Comment,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("proxied")] bool Proxied,
        [property: JsonPropertyName("id")] string Id);

    record RecordList([property: JsonPropertyName("result")] List<DnsRecord> Records)
    {
        public DnsRecord this[int index]
        {
            get
            {
                return Records[index];
            }
        }
    }
}
