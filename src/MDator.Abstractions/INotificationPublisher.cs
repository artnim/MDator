using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MDator;

/// <summary>
/// A single pending invocation of a notification handler: the already-closed-over
/// handler plus the notification instance. The publisher strategy decides how to
/// schedule these.
/// </summary>
public readonly struct NotificationHandlerExecutor(
    object handlerInstance,
    System.Func<INotification, CancellationToken, Task> callback)
{
  /// <summary>
  /// The handler instance. Exposed so publishers can do identity-based dedupe
  /// when the same handler is subscribed to multiple base notification types.
  /// </summary>
  public object HandlerInstance { get; } = handlerInstance;

  /// <summary>
  /// Invokes the handler with the notification.
  /// </summary>
  public System.Func<INotification, CancellationToken, Task> HandlerCallback { get; } = callback;
}

/// <summary>
/// Strategy for dispatching a notification to its handlers. Swap between
/// sequential, parallel-wait, and parallel-no-wait semantics without touching
/// handler code.
/// </summary>
public interface INotificationPublisher
{
  /// <summary>
  /// Publishes the given notification to all provided handlers asynchronously.
  /// </summary>
  /// <param name="handlerExecutors">
  /// The notification handler executors that define the instances and callback logic for handling the notification.
  /// </param>
  /// <param name="notification">
  /// The notification to be published to the registered handlers.
  /// </param>
  /// <param name="cancellationToken">
  /// A token to observe while waiting for the task to complete. It allows the operation to be canceled if required.
  /// </param>
  /// <returns>
  /// A task that represents the asynchronous operation of publishing the notification.
  /// </returns>
  Task Publish(
      IEnumerable<NotificationHandlerExecutor> handlerExecutors,
      INotification notification,
      CancellationToken cancellationToken);
}
