using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JoshHeaps.Net.Pages
{
    public class IndexModel(IBlogService blogService) : PageModel
    {
        public List<BlogPost> LatestPosts { get; set; } = [];

        public async Task OnGetAsync()
        {
            var allPosts = await blogService.GetAllPostsAsync();
            LatestPosts = allPosts.Take(3).ToList();
        }
    }
}
