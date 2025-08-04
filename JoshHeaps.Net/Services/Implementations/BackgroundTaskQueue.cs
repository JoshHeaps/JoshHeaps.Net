using JoshHeaps.Net.Services.Interfaces;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace JoshHeaps.Net.Services.Implementations;

public class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly ConcurrentDictionary<int, Task> _runningTasks = new();

    public void Queue(Func<Task> workItem)
    {
        var task = Task.Run(workItem);
        _runningTasks.TryAdd(task.Id, task);

        task.ContinueWith(t => _runningTasks.TryRemove(t.Id, out _), TaskScheduler.Default);
    }

    public IReadOnlyCollection<Task> Running => [.. _runningTasks.Values];
    public Task WhenAllDone() => Task.WhenAll(Running);
}
