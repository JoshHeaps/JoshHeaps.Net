using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Services.Implementations;
using JoshHeaps.Net.Services.Interfaces;

namespace JoshHeaps.Net;

// need to declare the class so I can add the public static bool
public class Program
{
    public static bool CheckingForIpUpdates { get; private set; } = false;

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddRazorPages();
        var configuration = builder.Configuration;
        Task updateIpTask;

        if (!builder.Environment.IsDevelopment())
            updateIpTask = Run(configuration);

        builder.Services.AddControllers();

        builder.Services.AddSignalR();

        builder.Services.AddSingleton<IChessService, ChessService>();
        builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
                ctx.Context.Response.Headers.Append("Pragma", "no-cache");
                ctx.Context.Response.Headers.Append("Expires", "0");
            }
        });

        app.UseRouting();

        app.UseAuthorization();

        app.MapRazorPages();

        app.MapControllers();

        app.MapHub<ChessHub>("/chessHub");

        app.Run();
    }

    static async Task Run(IConfiguration config)
    {
        CheckingForIpUpdates = true;
        HttpClient httpClient = new();
        AAAARecord dnsRecord = await GetDnsRecordAsync(config);
        string lastKnownIp = dnsRecord.content;
        TimeSpan checkInterval = TimeSpan.FromMinutes(1);

        while (true)
        {
            try
            {
                string currentIp = await GetPublicIpAsync(httpClient) ?? "";

                if (lastKnownIp != currentIp)
                {
                    await UpdateDnsIpAsync(config, dnsRecord, currentIp);

                    lastKnownIp = currentIp;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            await Task.Delay(checkInterval);
        }
    }

    static async Task<string> GetPublicIpAsync(HttpClient client)
    {
        try
        {
            return await client.GetStringAsync(@"https://api.ipify.org/");
        }
        catch
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("Reattempting to grab public ip");

            return await GetPublicIpAsync(client);
        }
    }

    static async Task<AAAARecord> GetDnsRecordAsync(IConfiguration config)
    {
        try
        {
            HttpClient cfClient = new();
            cfClient.DefaultRequestHeaders.Add("X-Auth-Email", config["cfEmail"]);
            cfClient.DefaultRequestHeaders.Add("X-Auth-Key", config["cfKey"]);
            var result = await cfClient.GetAsync(@$"https://api.cloudflare.com/client/v4/zones/{config["zoneId"]}/dns_records");
            Console.WriteLine(await result.Content.ReadAsStringAsync());
            var records = System.Text.Json.JsonSerializer.Deserialize<RecordList>(await result.Content.ReadAsStringAsync());
            return records!.result[0];
        }
        catch
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("Reattempting to grab current ip");

            return await GetDnsRecordAsync(config);
        }
    }

    static async Task UpdateDnsIpAsync(IConfiguration config, AAAARecord record, string ip)
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

        var result = await cfClient.PutAsJsonAsync(@$"https://api.cloudflare.com/client/v4/zones/{config["zoneId"]}/dns_records{record.id}", content);

        if (!result.IsSuccessStatusCode)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("Reattempting to update ip");

            await UpdateDnsIpAsync(config, record, ip);
        }
    }

    record AAAARecord(string comment, string content, string name, string id);

    record RecordList(List<AAAARecord> result);
}
