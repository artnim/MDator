using Microsoft.Extensions.DependencyInjection;

namespace MDator.Tests;

// ── Fixtures ──────────────────────────────────────────────────────────

public record TokenProbeQuery() : IRequest<string>;

/// <summary>Records the CancellationTokens observed along the pipeline.</summary>
public sealed class TokenProbe
{
  public CancellationToken HandlerToken { get; set; }
  public CancellationToken BehaviorToken { get; set; }
}

public sealed class TokenProbeHandler : IRequestHandler<TokenProbeQuery, string>
{
  private readonly TokenProbe _probe;
  public TokenProbeHandler(TokenProbe probe) => _probe = probe;
  public Task<string> Handle(TokenProbeQuery request, CancellationToken ct)
  {
    _probe.HandlerToken = ct;
    return Task.FromResult("ok");
  }
}

/// <summary>MediatR 12.3+ style behavior that passes its own token to next().</summary>
public class TokenSwappingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
  public static readonly CancellationTokenSource Cts = new();
  public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
      => next(Cts.Token);
}

/// <summary>Records the token the next pipeline step actually receives.</summary>
public class TokenRecordingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
  private readonly TokenProbe _probe;
  public TokenRecordingBehavior(TokenProbe probe) => _probe = probe;
  public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
  {
    _probe.BehaviorToken = ct;
    return next();
  }
}

public record PlainTokenQuery() : IRequest<string>;

public sealed class PlainTokenHandler : IRequestHandler<PlainTokenQuery, string>
{
  private readonly TokenProbe _probe;
  public PlainTokenHandler(TokenProbe probe) => _probe = probe;
  public Task<string> Handle(PlainTokenQuery request, CancellationToken ct)
  {
    _probe.HandlerToken = ct;
    return Task.FromResult("ok");
  }
}

public record SyncNotification(string Name) : INotification;

/// <summary>Uses MediatR's NotificationHandler&lt;T&gt; sync convenience base.</summary>
public sealed class SyncNotificationHandler : NotificationHandler<SyncNotification>
{
  private readonly Log _log;
  public SyncNotificationHandler(Log log) => _log = log;
  protected override void Handle(SyncNotification notification)
      => _log.Entries.Add($"sync:{notification.Name}");
}

public record OpenBehaviorProbeQuery() : IRequest<int>;

public sealed class OpenBehaviorProbeHandler : IRequestHandler<OpenBehaviorProbeQuery, int>
{
  public Task<int> Handle(OpenBehaviorProbeQuery request, CancellationToken ct) => Task.FromResult(1);
}

/// <summary>
/// Open generic behavior NOT declared via [assembly: OpenBehavior] — only
/// reachable when registered through cfg.AddOpenBehavior, as in MediatR.
/// </summary>
public class ConfigRegisteredBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
  private readonly Log _log;
  public ConfigRegisteredBehavior(Log log) => _log = log;
  public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
  {
    _log.Entries.Add($"open-behavior:{typeof(TRequest).Name}");
    return next();
  }
}

public record CompatTicks(int Count) : IStreamRequest<int>;

public sealed class CompatTicksHandler : IStreamRequestHandler<CompatTicks, int>
{
  public async IAsyncEnumerable<int> Handle(CompatTicks request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
  {
    for (var i = 0; i < request.Count; i++) yield return i;
    await Task.CompletedTask;
  }
}

/// <summary>
/// Open generic stream behavior — not auto-discovered by the generator, so it
/// only runs when registered via cfg.AddOpenStreamBehavior, as in MediatR.
/// </summary>
public class LoggingStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
  private readonly Log _log;
  public LoggingStreamBehavior(Log log) => _log = log;
  public async IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
  {
    _log.Entries.Add($"stream-behavior:{typeof(TRequest).Name}");
    await foreach (var item in next().WithCancellation(ct)) yield return item;
  }
}

/// <summary>Publisher registered by type via cfg.NotificationPublisherType.</summary>
public sealed class RecordingPublisher : INotificationPublisher
{
  private readonly Log _log;
  public RecordingPublisher(Log log) => _log = log;
  public async Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
  {
    _log.Entries.Add("recording-publisher");
    foreach (var executor in handlerExecutors)
      await executor.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
  }
}

// ── Tests ─────────────────────────────────────────────────────────────

public class MediatRCompatTests
{
  [Fact]
  public async Task Behavior_can_override_the_token_via_next()
  {
    // Registration order matters: the swapper must wrap the recorder, so the
    // recorder observes what the swapper passed to next(). Like MediatR, the
    // override only travels until a step calls next() without forwarding it.
    var sp = Rebuild(new TokenProbe(), cfg =>
    {
      cfg.AddOpenBehavior(typeof(TokenRecordingBehavior<,>));
      cfg.AddOpenBehavior(typeof(TokenSwappingBehavior<,>));
    });
    var mediator = sp.GetRequiredService<IMediator>();

    using var ambient = new CancellationTokenSource();
    await mediator.Send(new TokenProbeQuery(), ambient.Token);

    var recorded = sp.GetRequiredService<TokenProbe>().BehaviorToken;
    Assert.Equal(TokenSwappingBehavior<TokenProbeQuery, string>.Cts.Token, recorded);
    Assert.NotEqual(ambient.Token, recorded);
  }

  [Fact]
  public async Task Ambient_token_flows_when_next_called_without_argument()
  {
    var sp = Rebuild(new TokenProbe(), configure: null);
    var mediator = sp.GetRequiredService<IMediator>();

    using var ambient = new CancellationTokenSource();
    await mediator.Send(new PlainTokenQuery(), ambient.Token);

    Assert.Equal(ambient.Token, sp.GetRequiredService<TokenProbe>().HandlerToken);
  }

  [Fact]
  public async Task NotificationHandler_base_class_dispatches_synchronously()
  {
    var sp = TestServices.Build();
    var mediator = sp.GetRequiredService<IMediator>();

    await mediator.Publish(new SyncNotification("hello"));

    Assert.Contains("sync:hello", sp.GetRequiredService<Log>().Entries);
  }

  [Fact]
  public async Task AddOpenBehavior_registers_an_open_generic_behavior()
  {
    var sp = TestServices.Build(cfg => cfg.AddOpenBehavior(typeof(ConfigRegisteredBehavior<,>)));
    var mediator = sp.GetRequiredService<IMediator>();

    var result = await mediator.Send(new OpenBehaviorProbeQuery());

    Assert.Equal(1, result);
    Assert.Contains("open-behavior:OpenBehaviorProbeQuery", sp.GetRequiredService<Log>().Entries);
  }

  [Fact]
  public void AddOpenBehavior_rejects_non_behavior_types()
  {
    Assert.Throws<InvalidOperationException>(
        () => new MDatorConfiguration().AddOpenBehavior(typeof(List<>)));
  }

  [Fact]
  public async Task AddOpenStreamBehavior_registers_an_open_stream_behavior()
  {
    var sp = TestServices.Build(cfg => cfg.AddOpenStreamBehavior(typeof(LoggingStreamBehavior<,>)));
    var mediator = sp.GetRequiredService<IMediator>();

    var items = new List<int>();
    await foreach (var i in mediator.CreateStream(new CompatTicks(3))) items.Add(i);

    Assert.Equal([0, 1, 2], items);
    Assert.Contains("stream-behavior:CompatTicks", sp.GetRequiredService<Log>().Entries);
  }

  [Fact]
  public void AddStreamBehavior_maps_the_stream_behavior_interfaces()
  {
    var cfg = new MDatorConfiguration();
    cfg.AddStreamBehavior(typeof(LoggingStreamBehavior<CompatTicks, int>));

    Assert.Contains(cfg.AdditionalBehaviors, x =>
        x.ServiceType == typeof(IStreamPipelineBehavior<CompatTicks, int>) &&
        x.ImplementationType == typeof(LoggingStreamBehavior<CompatTicks, int>));
  }

  [Fact]
  public async Task NotificationPublisherType_is_resolved_from_DI()
  {
    var sp = TestServices.Build(cfg => cfg.NotificationPublisherType = typeof(RecordingPublisher));
    var mediator = sp.GetRequiredService<IMediator>();

    await mediator.Publish(new SyncNotification("via-type"));

    var log = sp.GetRequiredService<Log>().Entries;
    Assert.Contains("recording-publisher", log);
    Assert.Contains("sync:via-type", log);
  }

  [Fact]
  public void ForeachAwaitPublisher_uses_MediatR_spelling()
  {
    // Compile-level parity check: the MediatR class name must exist and be the default.
    INotificationPublisher publisher = new ForeachAwaitPublisher();
    Assert.IsType<ForeachAwaitPublisher>(new MDatorConfiguration().NotificationPublisher, exactMatch: true);
    Assert.NotNull(publisher);
  }

  /// <summary>Builds a container with a specific TokenProbe singleton.</summary>
  private static ServiceProvider Rebuild(TokenProbe probe, Action<MDatorConfiguration>? configure)
  {
    var services = new ServiceCollection();
    services.AddSingleton<Log>();
    services.AddSingleton(probe);
    services.AddMDator(configure);
    return services.BuildServiceProvider();
  }
}
