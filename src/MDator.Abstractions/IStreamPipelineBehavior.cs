using System.Collections.Generic;
using System.Threading;

namespace MDator;

/// <summary>
/// The next step in a stream request pipeline.
/// </summary>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<out TResponse>();

/// <summary>
/// Wraps a stream request handler for cross-cutting concerns.
/// </summary>
/// <typeparam name="TRequest">The type of the request being handled.</typeparam>
/// <typeparam name="TResponse">The type of the response produced by the pipeline.</typeparam>
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
  /// <summary>
  /// Processes a stream request, applies any pipeline behaviors, and invokes the next step in the pipeline.
  /// </summary>
  /// <param name="request">The request instance to be processed.</param>
  /// <param name="next">A delegate representing the next step in the pipeline.</param>
  /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
  /// <returns>An asynchronous stream of responses of type <typeparamref name="TResponse"/>.</returns>
  IAsyncEnumerable<TResponse> Handle(
      TRequest request,
      StreamHandlerDelegate<TResponse> next,
      CancellationToken cancellationToken);
}
