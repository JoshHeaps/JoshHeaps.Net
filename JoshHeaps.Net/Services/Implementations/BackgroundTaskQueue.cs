using JoshHeaps.Net.Services.Interfaces;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace JoshHeaps.Net.Services.Implementations;

public class BackgroundTaskQueue
{
    private readonly ConcurrentDictionary<Guid, Task> _runningTasks = new();

    public void Queue(Func<Task> workItem, Guid workId)
    {
        if (_runningTasks.ContainsKey(workId))
            return;

        var task = Task.Run(workItem);
        _runningTasks.TryAdd(workId, task);

        task.ContinueWith(t => _runningTasks.TryRemove(workId, out _), TaskScheduler.Default);
    }

    public IReadOnlyDictionary<Guid, Task> Running => _runningTasks;
    public Task WhenAllDone() => Task.WhenAll(Running.Values);
}
