namespace MDator;

/// <summary>
/// Invokes handlers sequentially, awaiting each one before starting the next.
/// The first exception propagates immediately and remaining handlers are not run.
/// Matches MediatR's default <c>ForeachAwaitPublisher</c>.
/// </summary>
public class ForeachAwaitPublisher : INotificationPublisher
{
  /// <inheritdoc />
  public async Task Publish(
      IEnumerable<NotificationHandlerExecutor> handlerExecutors,
      INotification notification,
      CancellationToken cancellationToken)
  {
    foreach (var executor in handlerExecutors)
    {
      await executor.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
    }
  }
}

/// <summary>
/// Legacy MDator spelling of <see cref="ForeachAwaitPublisher"/>.
/// </summary>
[Obsolete("Use ForeachAwaitPublisher (MediatR spelling). This alias will be removed in 1.0.")]
public sealed class ForEachAwaitPublisher : ForeachAwaitPublisher;
