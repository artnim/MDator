using System.Threading;
using System.Threading.Tasks;

namespace MDator;

/// <summary>
/// Handles a single <typeparamref name="TNotification"/>. Multiple handlers may exist
/// for the same notification type; the active <see cref="INotificationPublisher"/>
/// decides how they are invoked.
/// </summary>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
  /// <summary>
  /// Handles a notification of type <typeparamref name="TNotification"/>.
  /// </summary>
  /// <param name="notification">
  /// The notification instance to process.
  /// </param>
  /// <param name="cancellationToken">
  /// A token to observe for cancellation requests.
  /// </param>
  /// <return>
  /// A <see cref="Task"/> that completes when the notification handling is finished.
  /// </return>
  Task Handle(TNotification notification, CancellationToken cancellationToken);
}

/// <summary>
/// Convenience base class for a synchronous notification handler, mirroring
/// MediatR's <c>NotificationHandler&lt;TNotification&gt;</c>.
/// </summary>
/// <typeparam name="TNotification">The notification type being handled.</typeparam>
public abstract class NotificationHandler<TNotification> : INotificationHandler<TNotification>
    where TNotification : INotification
{
  Task INotificationHandler<TNotification>.Handle(TNotification notification, CancellationToken cancellationToken)
  {
    Handle(notification);
    return Task.CompletedTask;
  }

  /// <summary>
  /// Override in a derived class with the synchronous handler logic.
  /// </summary>
  /// <param name="notification">The notification instance to process.</param>
  protected abstract void Handle(TNotification notification);
}
