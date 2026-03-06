using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JoshHeaps.Net.Pages;

public class BlogModel : PageModel
{
    private readonly IBlogService _blogService;

    public List<BlogPost> Posts { get; set; } = [];
    public string? ActiveTag { get; set; }

    public BlogModel(IBlogService blogService)
    {
        _blogService = blogService;
    }

    public async Task OnGetAsync(string? tag)
    {
        ActiveTag = tag;
        Posts = tag is not null
            ? await _blogService.GetPostsByTagAsync(tag)
            : await _blogService.GetAllPostsAsync();
    }
}
