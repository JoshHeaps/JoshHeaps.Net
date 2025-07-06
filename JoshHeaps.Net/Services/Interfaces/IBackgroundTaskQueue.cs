namespace JoshHeaps.Net.Services.Interfaces;

public interface IBackgroundTaskQueue
{
    void Queue(Func<Task> workItem, Guid workId);
}
