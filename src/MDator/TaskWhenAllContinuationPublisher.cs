namespace MDator;

/// <summary>
/// Starts every handler (synchronously up to the first await), then returns a
/// task that waits for all of them. Handlers race until their first await point,
/// after which the continuation scheduler owns them. Matches MediatR's
/// <c>TaskWhenAllPublisher</c> non-wait mode.
/// </summary>
public class TaskWhenAllContinuationPublisher : INotificationPublisher
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
      var current = executor;
      tasks.Add(Task.Run(() => current.HandlerCallback(notification, cancellationToken), cancellationToken));
    }
    return Task.WhenAll(tasks);
  }
}
