using JoshHeaps.Net.Models;

namespace JoshHeaps.Net.Services.Interfaces;

public interface IBlogService
{
    Task<List<BlogPost>> GetAllPostsAsync();
    Task<BlogPost?> GetPostBySlugAsync(string slug);
    Task<List<BlogPost>> GetPostsByTagAsync(string tag);
}
