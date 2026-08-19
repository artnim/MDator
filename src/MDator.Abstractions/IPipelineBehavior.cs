using System.Threading;
using System.Threading.Tasks;

namespace MDator;

/// <summary>
/// A delegate representing the next step in a request pipeline. Calling it advances
/// to the next behavior (or finally the request handler). Passing a non-default
/// <paramref name="t"/> replaces the cancellation token observed by the remaining
/// pipeline steps and the handler, matching MediatR v12.3+ semantics.
/// </summary>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken t = default);

/// <summary>
/// Wraps a request handler, allowing cross-cutting concerns (logging, validation,
/// caching, transactions, etc.) to be composed around it.
/// </summary>
/// <typeparam name="TRequest">The type of the request object being processed.</typeparam>
/// <typeparam name="TResponse">The type of the response object to be returned.</typeparam>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
  /// <summary>
  /// Processes a request through a behavior pipeline and invokes the next delegate in the chain.
  /// </summary>
  /// <param name="request">The request object to process.</param>
  /// <param name="next">The delegate representing the next action in the pipeline to invoke after this behavior completes.</param>
  /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
  /// <returns>A task that represents the asynchronous operation. On completion, returns the response of type <typeparamref name="TResponse"/>.</returns>
  Task<TResponse> Handle(
      TRequest request,
      RequestHandlerDelegate<TResponse> next,
      CancellationToken cancellationToken);
}
