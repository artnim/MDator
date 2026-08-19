using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace MDator;

/// <summary>
/// DI-based runtime dispatch for request types not advertised via
/// <c>[assembly: KnownRequest]</c> on any referenced assembly. Most cross-assembly
/// requests are now handled by the compile-time switch (the generator reads
/// <see cref="KnownRequestAttribute"/> from referenced assemblies). This fallback
/// only activates for truly dynamic or plugin-loaded request types. Called from
/// generated code only.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RuntimeDispatch
{
  // ── Cached MethodInfo for the typed fallback workers ─────────────────

  private static readonly MethodInfo s_sendTyped =
      typeof(RuntimeDispatch).GetMethod(nameof(SendFallbackTyped), BindingFlags.Static | BindingFlags.NonPublic)!;

  private static readonly MethodInfo s_sendVoidTyped =
      typeof(RuntimeDispatch).GetMethod(nameof(SendVoidFallbackTyped), BindingFlags.Static | BindingFlags.NonPublic)!;

  private static readonly MethodInfo s_streamTyped =
      typeof(RuntimeDispatch).GetMethod(nameof(StreamFallbackTyped), BindingFlags.Static | BindingFlags.NonPublic)!;

  private static readonly MethodInfo s_streamObjectTyped =
      typeof(RuntimeDispatch).GetMethod(nameof(StreamObjectFallbackTyped), BindingFlags.Static | BindingFlags.NonPublic)!;

  private static readonly MethodInfo s_publishTyped =
      typeof(RuntimeDispatch).GetMethod(nameof(PublishFallbackTyped), BindingFlags.Static | BindingFlags.NonPublic)!;

  private static readonly MethodInfo s_wrapToObjectTask =
      typeof(RuntimeDispatch).GetMethod(nameof(WrapToObjectTask), BindingFlags.Static | BindingFlags.NonPublic)!;

  private static readonly MethodInfo s_wrapVoidToObjectTask =
      typeof(RuntimeDispatch).GetMethod(nameof(WrapVoidToObjectTask), BindingFlags.Static | BindingFlags.NonPublic)!;

  // ── Compiled-delegate caches ─────────────────────────────────────────
  //
  // First call per (TRequest, TResponse) pair builds an Expression-tree
  // wrapper that closes the open generic and casts the request to its
  // runtime type. Subsequent calls hit a ConcurrentDictionary lookup +
  // delegate invoke — no MakeGenericMethod, no MethodInfo.Invoke, no
  // object[] arg array.

  private delegate Task<TResponse> SendThunk<TResponse>(
      IServiceProvider sp, MDatorConfiguration cfg,
      IRequest<TResponse> request, CancellationToken ct);

  private delegate Task SendVoidThunk(
      IServiceProvider sp, MDatorConfiguration cfg,
      IRequest request, CancellationToken ct);

  private delegate Task<object?> SendObjectThunk(
      IServiceProvider sp, MDatorConfiguration cfg,
      object request, CancellationToken ct);

  private delegate IAsyncEnumerable<TResponse> StreamThunk<TResponse>(
      IServiceProvider sp, MDatorConfiguration cfg,
      IStreamRequest<TResponse> request, CancellationToken ct);

  private delegate IAsyncEnumerable<object?> StreamObjectThunk(
      IServiceProvider sp, MDatorConfiguration cfg,
      object request, CancellationToken ct);

  private delegate Task PublishThunk(
      IServiceProvider sp, INotificationPublisher publisher,
      INotification notification, CancellationToken ct);

  private static readonly ConcurrentDictionary<(Type Req, Type Resp), Delegate> s_sendCache = new();
  private static readonly ConcurrentDictionary<Type, SendVoidThunk> s_sendVoidCache = new();
  private static readonly ConcurrentDictionary<Type, SendObjectThunk> s_sendObjectCache = new();
  private static readonly ConcurrentDictionary<(Type Req, Type Resp), Delegate> s_streamCache = new();
  private static readonly ConcurrentDictionary<Type, StreamObjectThunk> s_streamObjectCache = new();
  private static readonly ConcurrentDictionary<Type, PublishThunk> s_publishCache = new();

  // ── Request with response ───────────────────────────────────────────

  /// <summary>
  /// Fallback for <c>Send&lt;TResponse&gt;(IRequest&lt;TResponse&gt;)</c> when
  /// the request type is not in the compile-time switch.
  /// </summary>
  public static Task<TResponse> SendFallback<TResponse>(
      IServiceProvider sp, MDatorConfiguration cfg,
      IRequest<TResponse> request, CancellationToken ct)
  {
    var thunk = (SendThunk<TResponse>)s_sendCache.GetOrAdd(
        (request.GetType(), typeof(TResponse)),
        BuildSendThunk);
    return thunk(sp, cfg, request, ct);
  }

  private static Delegate BuildSendThunk((Type Req, Type Resp) key)
  {
    var (reqType, respType) = key;
    var typedMethod = s_sendTyped.MakeGenericMethod(reqType, respType);
    var delegateType = typeof(SendThunk<>).MakeGenericType(respType);

    var spP = Expression.Parameter(typeof(IServiceProvider), "sp");
    var cfgP = Expression.Parameter(typeof(MDatorConfiguration), "cfg");
    var reqP = Expression.Parameter(typeof(IRequest<>).MakeGenericType(respType), "req");
    var ctP = Expression.Parameter(typeof(CancellationToken), "ct");

    var call = Expression.Call(typedMethod, spP, cfgP, Expression.Convert(reqP, reqType), ctP);
    return Expression.Lambda(delegateType, call, spP, cfgP, reqP, ctP).Compile();
  }

  private static async Task<TResponse> SendFallbackTyped<TRequest, TResponse>(
      IServiceProvider sp, MDatorConfiguration cfg,
      TRequest request, CancellationToken ct)
      where TRequest : notnull, IRequest<TResponse>
  {
    var handler = sp.GetRequiredService<IRequestHandler<TRequest, TResponse>>();

    RequestHandlerDelegate<TResponse> next = async t =>
    {
      // MediatR v12.3+ semantics: a behavior may pass its own token to next();
      // default means "keep the ambient token".
      var effectiveCt = t == default ? ct : t;
      foreach (var p in sp.GetServices<IRequestPreProcessor<TRequest>>())
        await p.Process(request, effectiveCt).ConfigureAwait(false);

      var resp = await handler.Handle(request, effectiveCt).ConfigureAwait(false);

      foreach (var p in sp.GetServices<IRequestPostProcessor<TRequest, TResponse>>())
        await p.Process(request, resp, effectiveCt).ConfigureAwait(false);

      return resp;
    };

    next = ChainRequestBehaviors(sp, cfg, request, ct, next);
    return await next().ConfigureAwait(false);
  }

  // ── Void request ────────────────────────────────────────────────────

  /// <summary>
  /// Fallback for <c>Send&lt;TRequest&gt;(TRequest)</c> (void) when the request
  /// type is not in the compile-time switch.
  /// </summary>
  public static Task SendVoidFallback<TRequest>(
      IServiceProvider sp, MDatorConfiguration cfg,
      TRequest request, CancellationToken ct)
      where TRequest : notnull, IRequest
  {
    var thunk = s_sendVoidCache.GetOrAdd(request!.GetType(), BuildSendVoidThunk);
    return thunk(sp, cfg, request, ct);
  }

  private static SendVoidThunk BuildSendVoidThunk(Type reqType)
  {
    var typedMethod = s_sendVoidTyped.MakeGenericMethod(reqType);

    var spP = Expression.Parameter(typeof(IServiceProvider), "sp");
    var cfgP = Expression.Parameter(typeof(MDatorConfiguration), "cfg");
    var reqP = Expression.Parameter(typeof(IRequest), "req");
    var ctP = Expression.Parameter(typeof(CancellationToken), "ct");

    var call = Expression.Call(typedMethod, spP, cfgP, Expression.Convert(reqP, reqType), ctP);
    return Expression.Lambda<SendVoidThunk>(call, spP, cfgP, reqP, ctP).Compile();
  }

  private static async Task SendVoidFallbackTyped<TRequest>(
      IServiceProvider sp, MDatorConfiguration cfg,
      TRequest request, CancellationToken ct)
      where TRequest : notnull, IRequest
  {
    var handler = sp.GetRequiredService<IRequestHandler<TRequest>>();

    RequestHandlerDelegate<Unit> next = async t =>
    {
      var effectiveCt = t == default ? ct : t;
      foreach (var p in sp.GetServices<IRequestPreProcessor<TRequest>>())
        await p.Process(request, effectiveCt).ConfigureAwait(false);

      await handler.Handle(request, effectiveCt).ConfigureAwait(false);
      return Unit.Value;
    };

    next = ChainVoidBehaviors(sp, cfg, request, ct, next);
    await next().ConfigureAwait(false);
  }

  // ── Send(object) ────────────────────────────────────────────────────

  /// <summary>
  /// Fallback for <c>Send(object)</c> when the request type is not in the
  /// compile-time switch.
  /// </summary>
  public static Task<object?> SendObjectFallback(
      IServiceProvider sp, MDatorConfiguration cfg,
      object request, CancellationToken ct)
  {
    var thunk = s_sendObjectCache.GetOrAdd(request.GetType(), BuildSendObjectThunk);
    return thunk(sp, cfg, request, ct);
  }

  private static SendObjectThunk BuildSendObjectThunk(Type reqType)
  {
    var spP = Expression.Parameter(typeof(IServiceProvider), "sp");
    var cfgP = Expression.Parameter(typeof(MDatorConfiguration), "cfg");
    var reqP = Expression.Parameter(typeof(object), "req");
    var ctP = Expression.Parameter(typeof(CancellationToken), "ct");
    var castReq = Expression.Convert(reqP, reqType);

    // IRequest<TResponse> takes precedence (matches original behavior).
    foreach (var iface in reqType.GetInterfaces())
    {
      if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IRequest<>))
      {
        var respType = iface.GetGenericArguments()[0];
        var typedMethod = s_sendTyped.MakeGenericMethod(reqType, respType);
        var wrap = s_wrapToObjectTask.MakeGenericMethod(respType);

        var typedCall = Expression.Call(typedMethod, spP, cfgP, castReq, ctP);
        var body = Expression.Call(wrap, typedCall);
        return Expression.Lambda<SendObjectThunk>(body, spP, cfgP, reqP, ctP).Compile();
      }
    }

    if (typeof(IRequest).IsAssignableFrom(reqType))
    {
      var typedMethod = s_sendVoidTyped.MakeGenericMethod(reqType);
      var voidCall = Expression.Call(typedMethod, spP, cfgP, castReq, ctP);
      var body = Expression.Call(s_wrapVoidToObjectTask, voidCall);
      return Expression.Lambda<SendObjectThunk>(body, spP, cfgP, reqP, ctP).Compile();
    }

    throw new InvalidOperationException(
        $"MDator: no handler known for request type '{reqType.FullName}'. " +
        "The type does not implement IRequest or IRequest<TResponse>.");
  }

  private static async Task<object?> WrapToObjectTask<TResponse>(Task<TResponse> task)
      => await task.ConfigureAwait(false);

  private static async Task<object?> WrapVoidToObjectTask(Task task)
  {
    await task.ConfigureAwait(false);
    return null;
  }

  // ── Stream with response ────────────────────────────────────────────

  /// <summary>
  /// Fallback for <c>CreateStream&lt;TResponse&gt;(IStreamRequest&lt;TResponse&gt;)</c>
  /// when the request type is not in the compile-time switch.
  /// </summary>
  public static IAsyncEnumerable<TResponse> StreamFallback<TResponse>(
      IServiceProvider sp, MDatorConfiguration cfg,
      IStreamRequest<TResponse> request, CancellationToken ct)
  {
    var thunk = (StreamThunk<TResponse>)s_streamCache.GetOrAdd(
        (request.GetType(), typeof(TResponse)),
        BuildStreamThunk);
    return thunk(sp, cfg, request, ct);
  }

  private static Delegate BuildStreamThunk((Type Req, Type Resp) key)
  {
    var (reqType, respType) = key;
    var typedMethod = s_streamTyped.MakeGenericMethod(reqType, respType);
    var delegateType = typeof(StreamThunk<>).MakeGenericType(respType);

    var spP = Expression.Parameter(typeof(IServiceProvider), "sp");
    var cfgP = Expression.Parameter(typeof(MDatorConfiguration), "cfg");
    var reqP = Expression.Parameter(typeof(IStreamRequest<>).MakeGenericType(respType), "req");
    var ctP = Expression.Parameter(typeof(CancellationToken), "ct");

    var call = Expression.Call(typedMethod, spP, cfgP, Expression.Convert(reqP, reqType), ctP);
    return Expression.Lambda(delegateType, call, spP, cfgP, reqP, ctP).Compile();
  }

  private static IAsyncEnumerable<TResponse> StreamFallbackTyped<TRequest, TResponse>(
      IServiceProvider sp, MDatorConfiguration cfg,
      TRequest request, CancellationToken ct)
      where TRequest : notnull, IStreamRequest<TResponse>
  {
    var handler = sp.GetRequiredService<IStreamRequestHandler<TRequest, TResponse>>();
    StreamHandlerDelegate<TResponse> next = () => handler.Handle(request, ct);

    next = ChainStreamBehaviors(sp, cfg, request, ct, next);
    return next();
  }

  // ── CreateStream(object) ────────────────────────────────────────────

  /// <summary>
  /// Fallback for <c>CreateStream(object)</c> when the request type is not
  /// in the compile-time switch.
  /// </summary>
  public static IAsyncEnumerable<object?> StreamObjectFallback(
      IServiceProvider sp, MDatorConfiguration cfg,
      object request, CancellationToken ct)
  {
    var thunk = s_streamObjectCache.GetOrAdd(request.GetType(), BuildStreamObjectThunk);
    return thunk(sp, cfg, request, ct);
  }

  private static StreamObjectThunk BuildStreamObjectThunk(Type reqType)
  {
    foreach (var iface in reqType.GetInterfaces())
    {
      if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IStreamRequest<>))
      {
        var respType = iface.GetGenericArguments()[0];
        var typedMethod = s_streamObjectTyped.MakeGenericMethod(reqType, respType);

        var spP = Expression.Parameter(typeof(IServiceProvider), "sp");
        var cfgP = Expression.Parameter(typeof(MDatorConfiguration), "cfg");
        var reqP = Expression.Parameter(typeof(object), "req");
        var ctP = Expression.Parameter(typeof(CancellationToken), "ct");

        var call = Expression.Call(typedMethod, spP, cfgP, Expression.Convert(reqP, reqType), ctP);
        return Expression.Lambda<StreamObjectThunk>(call, spP, cfgP, reqP, ctP).Compile();
      }
    }

    throw new InvalidOperationException(
        $"MDator: no stream handler known for request type '{reqType.FullName}'. " +
        "The type does not implement IStreamRequest<TResponse>.");
  }

  private static async IAsyncEnumerable<object?> StreamObjectFallbackTyped<TRequest, TResponse>(
      IServiceProvider sp, MDatorConfiguration cfg,
      TRequest request, [EnumeratorCancellation] CancellationToken ct)
      where TRequest : notnull, IStreamRequest<TResponse>
  {
    await foreach (var item in StreamFallbackTyped<TRequest, TResponse>(sp, cfg, request, ct)
        .WithCancellation(ct).ConfigureAwait(false))
    {
      yield return item;
    }
  }

  // ── Publish ─────────────────────────────────────────────────────────

  /// <summary>
  /// Fallback for <c>Publish&lt;TNotification&gt;(TNotification)</c> and
  /// <c>Publish(object)</c> when the notification type is not in the
  /// compile-time switch. Resolves <see cref="INotificationHandler{TNotification}"/>
  /// instances from DI for the runtime notification type and dispatches them
  /// through the active <see cref="INotificationPublisher"/>.
  /// </summary>
  public static Task PublishFallback(
      IServiceProvider sp, INotificationPublisher publisher,
      INotification notification, CancellationToken ct)
  {
    var thunk = s_publishCache.GetOrAdd(notification.GetType(), BuildPublishThunk);
    return thunk(sp, publisher, notification, ct);
  }

  private static PublishThunk BuildPublishThunk(Type notifType)
  {
    var typedMethod = s_publishTyped.MakeGenericMethod(notifType);

    var spP = Expression.Parameter(typeof(IServiceProvider), "sp");
    var pubP = Expression.Parameter(typeof(INotificationPublisher), "publisher");
    var nP = Expression.Parameter(typeof(INotification), "n");
    var ctP = Expression.Parameter(typeof(CancellationToken), "ct");

    var call = Expression.Call(typedMethod, spP, pubP, Expression.Convert(nP, notifType), ctP);
    return Expression.Lambda<PublishThunk>(call, spP, pubP, nP, ctP).Compile();
  }

  private static Task PublishFallbackTyped<TNotification>(
      IServiceProvider sp, INotificationPublisher publisher,
      TNotification notification, CancellationToken ct)
      where TNotification : INotification
  {
    var handlers = sp.GetServices<INotificationHandler<TNotification>>();
    var executors = new List<NotificationHandlerExecutor>();
    foreach (var h in handlers)
    {
      var capture = h;
      executors.Add(new NotificationHandlerExecutor(capture, (msg, c) => capture.Handle((TNotification)msg, c)));
    }
    return publisher.Publish(executors, notification, ct);
  }

  // ── Behavior chaining helpers ───────────────────────────────────────

  private static RequestHandlerDelegate<TResponse> ChainRequestBehaviors<TRequest, TResponse>(
      IServiceProvider sp, MDatorConfiguration cfg,
      TRequest request, CancellationToken ct,
      RequestHandlerDelegate<TResponse> next)
      where TRequest : notnull
  {
    // Open behaviors registered via [assembly: OpenBehavior(...)].
    // Reversed so that declared-earlier (lower Order) = outermost, matching
    // the compile-time fused pipeline ordering.
    foreach (var (openType, _) in cfg.OpenBehaviorTypes.OrderBy(x => x.Order).Reverse())
    {
      try
      {
        var closedType = openType.MakeGenericType(typeof(TRequest), typeof(TResponse));
        if (sp.GetService(closedType) is IPipelineBehavior<TRequest, TResponse> b)
        {
          var prev = next;
          next = t => b.Handle(request, prev, t == default ? ct : t);
        }
      }
      catch (ArgumentException)
      {
        // Open type may not accept these type arguments (e.g. additional
        // constraints on the open generic). Skip gracefully.
      }
    }

    // Runtime-added behaviors (skipped under FuseOnly).
    if (!cfg.FuseOnly)
    {
      foreach (var rb in sp.GetServices<IPipelineBehavior<TRequest, TResponse>>())
      {
        var prev = next;
        next = t => rb.Handle(request, prev, t == default ? ct : t);
      }
    }

    return next;
  }

  private static RequestHandlerDelegate<Unit> ChainVoidBehaviors<TRequest>(
      IServiceProvider sp, MDatorConfiguration cfg,
      TRequest request, CancellationToken ct,
      RequestHandlerDelegate<Unit> next)
      where TRequest : notnull
  {
    foreach (var (openType, _) in cfg.OpenBehaviorTypes.OrderBy(x => x.Order).Reverse())
    {
      try
      {
        var closedType = openType.MakeGenericType(typeof(TRequest), typeof(Unit));
        if (sp.GetService(closedType) is IPipelineBehavior<TRequest, Unit> b)
        {
          var prev = next;
          next = t => b.Handle(request, prev, t == default ? ct : t);
        }
      }
      catch (ArgumentException)
      {
        // Skip if type arguments don't match open generic constraints.
      }
    }

    if (!cfg.FuseOnly)
    {
      foreach (var rb in sp.GetServices<IPipelineBehavior<TRequest, Unit>>())
      {
        var prev = next;
        next = t => rb.Handle(request, prev, t == default ? ct : t);
      }
    }

    return next;
  }

  private static StreamHandlerDelegate<TResponse> ChainStreamBehaviors<TRequest, TResponse>(
      IServiceProvider sp, MDatorConfiguration cfg,
      TRequest request, CancellationToken ct,
      StreamHandlerDelegate<TResponse> next)
      where TRequest : notnull, IStreamRequest<TResponse>
  {
    foreach (var (openType, _) in cfg.OpenBehaviorTypes.OrderBy(x => x.Order).Reverse())
    {
      try
      {
        var closedType = openType.MakeGenericType(typeof(TRequest), typeof(TResponse));
        if (sp.GetService(closedType) is IStreamPipelineBehavior<TRequest, TResponse> b)
        {
          var prev = next;
          next = () => b.Handle(request, prev, ct);
        }
      }
      catch (ArgumentException)
      {
        // Skip if type arguments don't match open generic constraints.
      }
    }

    if (!cfg.FuseOnly)
    {
      foreach (var rb in sp.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>())
      {
        var prev = next;
        next = () => rb.Handle(request, prev, ct);
      }
    }

    return next;
  }
}
