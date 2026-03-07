using System.Collections.Concurrent;
using System.Text.Json;
using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;

namespace JoshHeaps.Net.Services.Implementations;

public class BlogService : IBlogService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BlogService> _logger;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BlogService(IHttpClientFactory httpClientFactory, ILogger<BlogService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("BlogApi");
        _logger = logger;
    }

    public async Task<List<BlogPost>> GetAllPostsAsync()
    {
        return await GetCachedAsync("all_posts",
            () => FetchAsync<List<BlogPost>>("api/blog/posts")) ?? [];
    }

    public async Task<BlogPost?> GetPostBySlugAsync(string slug)
    {
        return await GetCachedAsync($"post_{slug}",
            () => FetchAsync<BlogPost>($"api/blog/posts/{Uri.EscapeDataString(slug)}"));
    }

    public async Task<List<BlogPost>> GetPostsByTagAsync(string tag)
    {
        return await GetCachedAsync($"tag_{tag}",
            () => FetchAsync<List<BlogPost>>($"api/blog/posts/tags/{Uri.EscapeDataString(tag)}")) ?? [];
    }

    private async Task<T?> FetchAsync<T>(string path) where T : class
    {
        try
        {
            var response = await _httpClient.GetAsync(path);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Blog API returned {StatusCode} for {Path}", response.StatusCode, path);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch from blog API: {Path}", path);
            return null;
        }
    }

    private async Task<T?> GetCachedAsync<T>(string key, Func<Task<T?>> factory) where T : class
    {
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return (T?)entry.Value;

        var result = await factory();

        if (result is not null)
        {
            _cache[key] = new CacheEntry(result, DateTime.UtcNow.Add(_cacheTtl));
        }
        else if (entry is not null)
        {
            // API unreachable — serve stale cache
            _logger.LogWarning("Serving stale cache for key {Key}", key);
            return (T?)entry.Value;
        }

        return result;
    }

    public void ClearCache()
    {
        _cache.Clear();
        _logger.LogInformation("Blog cache cleared");
    }

    private record CacheEntry(object Value, DateTime ExpiresAt);
}
