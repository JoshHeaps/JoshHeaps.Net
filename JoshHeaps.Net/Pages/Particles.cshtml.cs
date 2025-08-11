using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JoshHeaps.Net.Pages;

public class ParticlesModel : PageModel
{
    public uint Seed { get; private set; } = 123456789u;
    public int N { get; private set; } = 600;
    public float Speed { get; private set; } = 1.0f;

    // GET /Particles?seed=123&n=800&speed=1.5
    public void OnGet(uint? seed, int? n, float? speed)
    {
        Seed = seed ?? Seed;
        N = Math.Clamp(n ?? N, 50, 5000);
        Speed = Math.Clamp(speed ?? Speed, 0.1f, 5f);
    }
}
