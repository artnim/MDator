namespace MDator;

/// <summary>
/// Invokes all handlers concurrently and awaits them with <see cref="Task.WhenAll(Task[])"/>.
/// Exceptions from all handlers are aggregated.
/// </summary>
public class TaskWhenAllPublisher : INotificationPublisher
{
  /// <inheritdoc />
  public Task Publish(
      IEnumerable<NotificationHandlerExecutor> handlerExecutors,
      INotification notification,
      CancellationToken cancellationToken)
  {
    var tasks = new List<Task>();
    foreach (var executor in handlerExecutors)
    {
      tasks.Add(executor.HandlerCallback(notification, cancellationToken));
    }
    return Task.WhenAll(tasks);
  }
}
