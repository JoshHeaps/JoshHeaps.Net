using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JoshHeaps.Net.Pages.Blog;

public class PostModel : PageModel
{
    private readonly IBlogService _blogService;

    public BlogPost? Post { get; set; }

    public PostModel(IBlogService blogService)
    {
        _blogService = blogService;
    }

    public async Task<IActionResult> OnGetAsync(string slug)
    {
        Post = await _blogService.GetPostBySlugAsync(slug);

        if (Post is null)
            return NotFound();

        return Page();
    }
}
