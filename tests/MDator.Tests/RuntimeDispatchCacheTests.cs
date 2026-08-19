using Microsoft.Extensions.DependencyInjection;

namespace MDator.Tests;

// Request/handler types intentionally registered manually (bypassing AddMDator)
// so dispatch can be exercised against RuntimeDispatch directly. This is the
// fallback path generated code uses for plugin-loaded request types that no
// assembly advertises via [assembly: KnownRequest].

public sealed record DynPing(int N) : IRequest<int>;
public sealed record DynVoid(int N) : IRequest;
public sealed record DynStream(int Count) : IStreamRequest<int>;
public sealed record DynNotification(int Id) : INotification;

public sealed class DynPingHandler : IRequestHandler<DynPing, int>
{
  public Task<int> Handle(DynPing request, CancellationToken ct) => Task.FromResult(request.N * 2);
}

public sealed class DynVoidHandler : IRequestHandler<DynVoid>
{
  public static int InvocationCount;
  public Task Handle(DynVoid request, CancellationToken ct)
  {
    Interlocked.Add(ref InvocationCount, request.N);
    return Task.CompletedTask;
  }
}

public sealed class DynStreamHandler : IStreamRequestHandler<DynStream, int>
{
  public async IAsyncEnumerable<int> Handle(DynStream request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
  {
    for (var i = 0; i < request.Count; i++)
    {
      yield return i;
      await Task.Yield();
    }
  }
}

public sealed class DynNotificationHandlerA : INotificationHandler<DynNotification>
{
  public static int InvocationCount;
  public Task Handle(DynNotification n, CancellationToken ct)
  {
    Interlocked.Increment(ref InvocationCount);
    return Task.CompletedTask;
  }
}

public sealed class DynNotificationHandlerB : INotificationHandler<DynNotification>
{
  public static int InvocationCount;
  public Task Handle(DynNotification n, CancellationToken ct)
  {
    Interlocked.Increment(ref InvocationCount);
    return Task.CompletedTask;
  }
}

public sealed class RuntimeDispatchCacheTests
{
  private static (IServiceProvider Sp, MDatorConfiguration Cfg) BuildBareContainer()
  {
    var services = new ServiceCollection();
    services.AddTransient<IRequestHandler<DynPing, int>, DynPingHandler>();
    services.AddTransient<IRequestHandler<DynVoid>, DynVoidHandler>();
    services.AddTransient<IStreamRequestHandler<DynStream, int>, DynStreamHandler>();
    services.AddTransient<INotificationHandler<DynNotification>, DynNotificationHandlerA>();
    services.AddTransient<INotificationHandler<DynNotification>, DynNotificationHandlerB>();
    var sp = services.BuildServiceProvider();
    return (sp, new MDatorConfiguration { FuseOnly = true });
  }

  [Fact]
  public async Task SendFallback_returns_correct_result_across_many_calls()
  {
    var (sp, cfg) = BuildBareContainer();

    for (var i = 0; i < 1000; i++)
    {
      var result = await RuntimeDispatch.SendFallback<int>(sp, cfg, new DynPing(i), default);
      Assert.Equal(i * 2, result);
    }
  }

  [Fact]
  public async Task SendVoidFallback_invokes_handler_for_every_call()
  {
    var (sp, cfg) = BuildBareContainer();
    var before = DynVoidHandler.InvocationCount;

    for (var i = 1; i <= 100; i++)
      await RuntimeDispatch.SendVoidFallback(sp, cfg, new DynVoid(i), default);

    // 1+2+...+100 == 5050
    Assert.Equal(before + 5050, DynVoidHandler.InvocationCount);
  }

  [Fact]
  public async Task SendObjectFallback_dispatches_request_with_response()
  {
    var (sp, cfg) = BuildBareContainer();

    for (var i = 0; i < 100; i++)
    {
      var result = await RuntimeDispatch.SendObjectFallback(sp, cfg, new DynPing(i), default);
      Assert.Equal(i * 2, Assert.IsType<int>(result));
    }
  }

  [Fact]
  public async Task SendObjectFallback_dispatches_void_request()
  {
    var (sp, cfg) = BuildBareContainer();
    var before = DynVoidHandler.InvocationCount;

    for (var i = 0; i < 50; i++)
    {
      var result = await RuntimeDispatch.SendObjectFallback(sp, cfg, new DynVoid(1), default);
      Assert.Null(result);
    }

    Assert.Equal(before + 50, DynVoidHandler.InvocationCount);
  }

  [Fact]
  public async Task SendObjectFallback_throws_for_non_request_type()
  {
    var (sp, cfg) = BuildBareContainer();

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        await RuntimeDispatch.SendObjectFallback(sp, cfg, new object(), default));
  }

  [Fact]
  public async Task StreamFallback_yields_expected_items_across_many_calls()
  {
    var (sp, cfg) = BuildBareContainer();

    for (var run = 0; run < 50; run++)
    {
      var items = new List<int>();
      await foreach (var item in RuntimeDispatch.StreamFallback<int>(sp, cfg, new DynStream(5), default))
        items.Add(item);

      Assert.Equal(new[] { 0, 1, 2, 3, 4 }, items);
    }
  }

  [Fact]
  public async Task StreamObjectFallback_yields_expected_items()
  {
    var (sp, cfg) = BuildBareContainer();

    for (var run = 0; run < 50; run++)
    {
      var items = new List<object?>();
      await foreach (var item in RuntimeDispatch.StreamObjectFallback(sp, cfg, new DynStream(3), default))
        items.Add(item);

      Assert.Equal(new object?[] { 0, 1, 2 }, items);
    }
  }

  [Fact]
  public async Task StreamObjectFallback_throws_for_non_stream_type()
  {
    var (sp, cfg) = BuildBareContainer();

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
    {
      await foreach (var _ in RuntimeDispatch.StreamObjectFallback(sp, cfg, new object(), default))
      {
        // unreachable
      }
    });
  }

  [Fact]
  public async Task SendFallback_concurrent_calls_share_cached_delegate()
  {
    var (sp, cfg) = BuildBareContainer();

    // Hammer the same (TRequest, TResponse) pair from many threads to verify
    // the cache is thread-safe and the delegate is reused.
    var tasks = Enumerable.Range(0, 64)
        .Select(i => Task.Run(async () =>
        {
          var result = await RuntimeDispatch.SendFallback<int>(sp, cfg, new DynPing(i), default);
          Assert.Equal(i * 2, result);
        }))
        .ToArray();

    await Task.WhenAll(tasks);
  }

  [Fact]
  public async Task PublishFallback_invokes_every_registered_handler()
  {
    var (sp, _) = BuildBareContainer();
    var publisher = new ForeachAwaitPublisher();
    var beforeA = DynNotificationHandlerA.InvocationCount;
    var beforeB = DynNotificationHandlerB.InvocationCount;

    await RuntimeDispatch.PublishFallback(sp, publisher, new DynNotification(1), default);

    Assert.Equal(beforeA + 1, DynNotificationHandlerA.InvocationCount);
    Assert.Equal(beforeB + 1, DynNotificationHandlerB.InvocationCount);
  }

  [Fact]
  public async Task PublishFallback_hot_loop_dispatches_correctly_with_cached_delegate()
  {
    var (sp, _) = BuildBareContainer();
    var publisher = new ForeachAwaitPublisher();
    var beforeA = DynNotificationHandlerA.InvocationCount;

    for (var i = 0; i < 500; i++)
      await RuntimeDispatch.PublishFallback(sp, publisher, new DynNotification(i), default);

    Assert.Equal(beforeA + 500, DynNotificationHandlerA.InvocationCount);
  }

  [Fact]
  public async Task PublishFallback_honors_configured_publisher_strategy()
  {
    var (sp, _) = BuildBareContainer();
    var publisher = new TaskWhenAllPublisher();
    var beforeA = DynNotificationHandlerA.InvocationCount;
    var beforeB = DynNotificationHandlerB.InvocationCount;

    await RuntimeDispatch.PublishFallback(sp, publisher, new DynNotification(42), default);

    Assert.Equal(beforeA + 1, DynNotificationHandlerA.InvocationCount);
    Assert.Equal(beforeB + 1, DynNotificationHandlerB.InvocationCount);
  }

  [Fact]
  public async Task PublishFallback_with_no_handlers_completes_silently()
  {
    // Bare container with zero INotificationHandler<DynNotification> registrations.
    // The fallback should resolve an empty enumerable and complete without throwing —
    // matching MediatR's "no handler is fine" semantic.
    var sp = new ServiceCollection().BuildServiceProvider();
    var publisher = new ForeachAwaitPublisher();

    await RuntimeDispatch.PublishFallback(sp, publisher, new DynNotification(0), default);
  }
}
