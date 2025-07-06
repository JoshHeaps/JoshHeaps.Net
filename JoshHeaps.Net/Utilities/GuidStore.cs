namespace JoshHeaps.Net.Utilities;

public static class GuidStore
{
    private static readonly SemaphoreSlim _mutex = new(1, 1);
    private static readonly List<Guid> _guids = [];

    public static async Task AddAsync(Guid id)
    {
        await _mutex.WaitAsync();
        try { _guids.Add(id); }
        finally { _mutex.Release(); }
    }

    public static async Task<List<Guid>> TakeAllAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            var copy = _guids.ToList();
            _guids.Clear();
            return copy;
        }
        finally { _mutex.Release(); }
    }

    public static async Task<bool> ContainsAsync(Guid id)
    {
        await _mutex.WaitAsync();
        try { return _guids.Contains(id); }
        finally { _mutex.Release(); }
    }

    public static async Task RemoveAsync(Guid id)
    {
        await _mutex.WaitAsync();
        try { _guids.Remove(id); }
        finally { _mutex.Release(); }
    }

    public static async Task<bool> AddIfAvailable(Guid id)
    {
        await _mutex.WaitAsync();
        try
        {
            if (!_guids.Contains(id))
            {
                _guids.Add(id);

                return true; // Successfully added
            }

            return false; // Already exists
        }
        finally { _mutex.Release(); }
    }
}