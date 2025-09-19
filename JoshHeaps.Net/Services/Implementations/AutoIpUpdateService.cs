using System.Text.Json;
using System.Text.Json.Serialization;

namespace JoshHeaps.Net.Services.Implementations;

public class AutoIpUpdateService(
        IConfiguration config,
        ILogger<AutoIpUpdateService> log)
    : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private static readonly HttpClient httpClient = new();
    public static bool IsEnabled { get; private set; } = false;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        IsEnabled = true;
        var timer = new PeriodicTimer(CheckInterval);
        AAAARecord dnsRecord = await GetDnsRecordAsync();
        string lastKnownIp = dnsRecord.Content;

        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                string currentIp = await GetPublicIpAsync() ?? "";

                if (lastKnownIp != currentIp)
                {
                    await UpdateDnsIpAsync(config, dnsRecord, currentIp);

                    lastKnownIp = currentIp;
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                log.LogError(ex, "Error while attempting ip update");
            }
        }
    }

    private static async Task<string> GetPublicIpAsync()
    {
        try
        {
            return await httpClient.GetStringAsync(@"https://api.ipify.org/");
        }
        catch
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("Reattempting to grab public ip");

            return await GetPublicIpAsync();
        }
    }

    private async Task<AAAARecord> GetDnsRecordAsync()
    {
        try
        {
            HttpClient cfClient = new();
            cfClient.DefaultRequestHeaders.Add("X-Auth-Email", config["cfEmail"]);
            cfClient.DefaultRequestHeaders.Add("X-Auth-Key", config["cfKey"]);
            var result = await cfClient.GetAsync(@$"https://api.cloudflare.com/client/v4/zones/{config["zoneId"]}/dns_records");
            Console.WriteLine(await result.Content.ReadAsStringAsync());
            var records = System.Text.Json.JsonSerializer.Deserialize<RecordList>(await result.Content.ReadAsStringAsync());
            return records!.Result[0];
        }
        catch
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("Reattempting to grab current ip");

            return await GetDnsRecordAsync();
        }
    }

    private static async Task UpdateDnsIpAsync(IConfiguration config, AAAARecord record, string ip)
    {
        HttpClient cfClient = new();
        cfClient.DefaultRequestHeaders.Add("X-Auth-Email", config["cfEmail"]);
        cfClient.DefaultRequestHeaders.Add("X-Auth-Key", config["cfKey"]);
        object content = new
        {
            comment = "Update as needed",
            content = ip,
            name = "@",
            proxied = true,
            ttl = 3600,
            type = "AAAA"
        };

        var result = await cfClient.PutAsJsonAsync(@$"https://api.cloudflare.com/client/v4/zones/{config["zoneId"]}/dns_records{record.Id}", content);

        if (!result.IsSuccessStatusCode)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("Reattempting to update ip");

            await UpdateDnsIpAsync(config, record, ip);
        }
    }

    record AAAARecord(
        [property: JsonPropertyName("comment")] string Comment,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("id")] string Id);

    record RecordList([property: JsonPropertyName("result")] List<AAAARecord> Result);
}
