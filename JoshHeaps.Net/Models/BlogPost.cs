namespace JoshHeaps.Net.Models;

public class BlogPost
{
    public long Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime PublishedDate { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public string HtmlContent { get; set; } = string.Empty;
}
