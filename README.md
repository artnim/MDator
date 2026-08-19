# MDator

A source-generated, MediatR-compatible mediator for .NET.

MDator replaces MediatR's runtime reflection with a C# incremental source
generator that discovers handlers, composes pipelines, and emits a strongly-typed
`IMediator` implementation at compile time. The public API mirrors MediatR v12
exactly -- migrating is a namespace find-replace:

```diff
- using MediatR;
+ using MDator;
```

## Why MDator?

| | MediatR v12 | MDator |
|---|---|---|
| Handler dispatch | Reflection + `MakeGenericType` + wrapper cache | Compile-time `switch` per request type |
| Pipeline composition | Runtime closure chain, services resolved reflectively | Generated per-request pipeline, behaviors fused at compile time |
| Open generic behaviors | Closed at runtime via DI's open-generic support | Fused by the generator into each concrete pipeline |
| Registration | Assembly scanning at startup | Source-generated `[ModuleInitializer]`, zero startup cost |
| License | Commercial (v13+) | MIT |

## Quick start

**1. Reference MDator** (the generator ships alongside the runtime library):

```xml
<PackageReference Include="MDator" Version="..." />
```

**2. Define a request and handler** -- identical to MediatR:

```csharp
using MDator;

public record GetUser(int Id) : IRequest<User>;

public sealed class GetUserHandler : IRequestHandler<GetUser, User>
{
    public Task<User> Handle(GetUser request, CancellationToken ct)
        => Task.FromResult(new User(request.Id, $"user-{request.Id}"));
}
```

**3. Register in your composition root:**

```csharp
services.AddMDator();
```

**4. Inject and use** `IMediator`, `ISender`, or `IPublisher`:

```csharp
var user = await mediator.Send(new GetUser(42));
```

At build time the generator emits a `GeneratedMediator` class that dispatches
`GetUser` directly to `GetUserHandler` -- no reflection, no dictionary lookups.

## Features

### Request / Response

```csharp
// With response
public record Ping(string Message) : IRequest<string>;
public class PingHandler : IRequestHandler<Ping, string> { ... }
var pong = await mediator.Send(new Ping("hello"));

// Void (no response)
public record FireAndForget() : IRequest;
public class FireHandler : IRequestHandler<FireAndForget> { ... }
await mediator.Send(new FireAndForget());

// Object-typed dispatch (runtime type switch, no reflection)
object request = new Ping("hi");
object? result = await mediator.Send(request);
```

### Notifications

```csharp
public record OrderPlaced(int OrderId) : INotification;

public class EmailNotifier : INotificationHandler<OrderPlaced> { ... }
public class AuditNotifier : INotificationHandler<OrderPlaced> { ... }

await mediator.Publish(new OrderPlaced(7)); // fans out to both handlers
```

Configure the publisher strategy at registration time:

```csharp
services.AddMDator(cfg =>
{
    // Sequential, stop on first error (default, matches MediatR):
    cfg.NotificationPublisher = new ForeachAwaitPublisher();

    // Parallel, aggregate all errors:
    cfg.NotificationPublisher = new TaskWhenAllPublisher();

    // Parallel via Task.Run, aggregate:
    cfg.NotificationPublisher = new TaskWhenAllContinuationPublisher();
});
```

### Open generic pipeline behaviors

The key differentiator. Declare behaviors with an assembly-level attribute so
the source generator can see them at compile time:

```csharp
[assembly: MDator.OpenBehavior(typeof(LoggingBehavior<,>), Order = 0)]
[assembly: MDator.OpenBehavior(typeof(ValidationBehavior<,>), Order = 1)]

public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        Console.WriteLine($"Handling {typeof(TRequest).Name}");
        var response = await next();
        Console.WriteLine($"Handled {typeof(TRequest).Name}");
        return response;
    }
}
```

The generator fuses `LoggingBehavior<GetUser, User>` directly into the
`SendCore_GetUser` pipeline -- no runtime generic closure, no
`MakeGenericType`, no allocation beyond the behavior's own closure captures.

`Order` controls nesting: lower values run outermost (closest to the caller).

### Closed pipeline behaviors

Non-generic behaviors targeting a specific `(TRequest, TResponse)` pair are
discovered by the generator's class scan and registered under
`IPipelineBehavior<TRequest, TResponse>`. They are picked up at runtime via
`IServiceProvider.GetServices<>()`:

```csharp
public class CacheBehavior : IPipelineBehavior<GetUser, User>
{
    public async Task<User> Handle(
        GetUser request, RequestHandlerDelegate<User> next, CancellationToken ct)
    {
        // check cache, call next(), populate cache
    }
}
```

### `FuseOnly` mode

For maximum performance, suppress the runtime enumeration fallback entirely:

```csharp
services.AddMDator(cfg => cfg.FuseOnly = true);
```

With `FuseOnly = true`, only `[assembly: OpenBehavior]`-declared behaviors
execute. Runtime-added behaviors are ignored, and the per-request
`GetServices<IPipelineBehavior<,>>()` call is eliminated.

### Pre / post processors

```csharp
public class AuditPreProcessor : IRequestPreProcessor<SaveItem>
{
    public Task Process(SaveItem request, CancellationToken ct) { ... }
}

public class AuditPostProcessor : IRequestPostProcessor<SaveItem, string>
{
    public Task Process(SaveItem request, string response, CancellationToken ct) { ... }
}
```

Pre-processors run before the handler; post-processors run after a successful
response, inside the innermost pipeline scope.

### Exception handlers and actions

```csharp
// Can convert an exception into a response:
public class RecoverHandler
    : IRequestExceptionHandler<SaveItem, string, InvalidOperationException>
{
    public Task Handle(
        SaveItem request,
        InvalidOperationException exception,
        RequestExceptionHandlerState<string> state,
        CancellationToken ct)
    {
        state.SetHandled("fallback-value");
        return Task.CompletedTask;
    }
}

// Observes exceptions without converting them (e.g. logging):
public class LogExceptionAction : IRequestExceptionAction<SaveItem, Exception>
{
    public Task Execute(SaveItem request, Exception exception, CancellationToken ct) { ... }
}
```

Exception handlers are ordered by type specificity at compile time
(most-derived first). Actions always run, even if a handler marks the
exception as handled.

### Streaming

```csharp
public record CountStream(int To) : IStreamRequest<int>;

public class CountHandler : IStreamRequestHandler<CountStream, int>
{
    public async IAsyncEnumerable<int> Handle(
        CountStream request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 1; i <= request.To; i++)
            yield return i;
    }
}

await foreach (var n in mediator.CreateStream(new CountStream(5)))
    Console.WriteLine(n); // 1, 2, 3, 4, 5
```

Stream pipeline behaviors (`IStreamPipelineBehavior<TRequest, TResponse>`) and
open stream behaviors via `[assembly: OpenBehavior]` are also supported.

## Configuration reference

```csharp
services.AddMDator(cfg =>
{
    // Lifetime for all generated registrations (default: Transient)
    cfg.Lifetime = ServiceLifetime.Scoped;

    // Notification publisher strategy (default: ForeachAwaitPublisher)
    cfg.NotificationPublisher = new TaskWhenAllPublisher();

    // Suppress runtime behavior enumeration for pure compile-time pipelines
    cfg.FuseOnly = true;

    // Add a closed behavior at runtime (e.g. from an unscanned assembly)
    cfg.AddBehavior<MyClosedBehavior>();
});
```

> **Note for migrators:** `RegisterServicesFromAssembly`,
> `RegisterServicesFromAssemblies`, and `RegisterServicesFromAssemblyContaining<T>`
> exist on `MDatorConfiguration` for MediatR source compatibility but have no
> effect — MDator's source generator scans the consuming compilation directly.
> The analyzer `MDATOR0001` flags these calls so they can be removed.

## Migrating from MediatR

1. Replace the `MediatR` package reference with `MDator`.
2. Find-replace `using MediatR;` with `using MDator;`.
3. Replace `services.AddMediatR(cfg => ...)` with `services.AddMDator(cfg => ...)`.
4. (Optional) Convert open behavior registrations from fluent to attribute.
   `cfg.AddOpenBehavior(typeof(LoggingBehavior<,>))` works as in MediatR, but
   runs on the runtime enumeration path; the attribute form fuses the behavior
   into the pipeline at compile time:

```diff
- cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
+ [assembly: MDator.OpenBehavior(typeof(LoggingBehavior<,>), Order = 0)]
```

5. Build. The generator discovers your handlers and emits the mediator.

All interface shapes (`IRequest<T>`, `IRequestHandler<,>`, `INotification`,
`INotificationHandler<>`, `IPipelineBehavior<,>`, `IStreamRequest<>`,
`IStreamRequestHandler<,>`, `IRequestPreProcessor<>`,
`IRequestPostProcessor<,>`, `IRequestExceptionHandler<,,>`,
`IRequestExceptionAction<,>`, `Unit`, `RequestHandlerDelegate<T>`
(including the optional `CancellationToken` parameter of MediatR 12.3+),
`StreamHandlerDelegate<T>`, `INotificationPublisher`, and the
`NotificationHandler<T>` base class) are source-level identical to MediatR v12.

Known divergences from MediatR v12:

- Everything lives in the single `MDator` namespace. Delete
  `using MediatR.Pipeline;` and `using MediatR.NotificationPublishers;` lines
  instead of find-replacing them.
- There is no public `Mediator` class to subclass (the mediator is generated);
  `MediatorImplementationType` and the scanning-tuning knobs
  (`MaxGenericTypeParameters`, `MaxTypesClosing`, …) do not exist.
- `AddRequestPreProcessor` / `AddRequestPostProcessor` config methods are not
  yet available — declare processor classes in a scanned assembly instead, and
  the generator wires them up.
- The processor behavior wrapper classes (`RequestPreProcessorBehavior<,>` and
  friends) are not public types; processors are fused directly.

## How it works

MDator ships three assemblies:

| Assembly | TFM | Purpose |
|---|---|---|
| `MDator.Abstractions` | netstandard2.0 | Interfaces, `Unit`, attributes. Reference this from handler libraries. |
| `MDator` | net9.0, net10.0 | Runtime shell: `AddMDator`, `MDatorConfiguration`, notification publishers. |
| `MDator.SourceGenerator` | netstandard2.0 | Roslyn incremental source generator, shipped as an analyzer. |

When you reference `MDator`, the generator activates in the consuming project
and emits a single file (`MDatorGenerated.g.cs`) containing:

- **`MDatorGeneratedRegistration`** -- a static class with a `[ModuleInitializer]`
  that registers every discovered handler, behavior, pre/post processor, and
  exception handler with `IServiceCollection`.
- **`GeneratedMediator`** -- a sealed `IMediator` implementation with:
  - A compile-time `switch` in `Send<TResponse>()` dispatching each known
    request type to a strongly-typed `SendCore_*` method.
  - Per-request pipeline methods that chain pre-processors, the handler,
    post-processors, fused open behaviors, runtime behaviors, and exception
    wrappers -- all as generated C# with zero reflection.
  - Analogous `PublishCore_*`, `StreamCore_*`, and `SendVoidCore_*` methods.

Multiple projects can each host handlers. Each project's generator emits its
own module initializer that appends to `MDatorGeneratedHook.Registrations`.
When `AddMDator()` runs in the composition root, all callbacks fire.

### Cross-assembly dispatch

When a handler lives in a different assembly from the mediator consumer, the
generator automatically propagates the request type across the project
boundary:

1. The handler assembly's generator emits
   `[assembly: KnownRequest(typeof(MyRequest))]` for every handler it
   discovers.
2. The consuming assembly's generator reads these attributes from its
   referenced assemblies and includes those types in its compile-time
   `switch` — generating strongly-typed `SendCore_*` pipeline methods with
   fused open behaviors, just like same-assembly requests.
3. A `RuntimeDispatch` fallback covers requests, streams, and notifications
   for plugin-loaded or otherwise dynamic types that no assembly advertises
   at compile time. The fallback caches a compiled, strongly-typed delegate
   per runtime type, so the warm path is close to the compile-time switch in
   cost. Notifications routed through `RuntimeDispatch.PublishFallback`
   resolve handlers from DI for the runtime type and dispatch through the
   active `INotificationPublisher` — no silent drops.

You can also apply `[assembly: KnownRequest(typeof(...))]` manually for
requests whose closed generic form is never syntactically referenced in the
consuming assembly.

## Project structure

```
MDator.slnx
src/
  MDator.Abstractions/     netstandard2.0 -- interfaces, Unit, attributes
  MDator/                  net9.0;net10.0 -- runtime, DI extensions, publishers
  MDator.SourceGenerator/  netstandard2.0 -- incremental generator (analyzer)
tests/
  MDator.Tests/                net10.0, xUnit -- integration tests
  MDator.Tests.CrossAssembly/  net10.0        -- cross-assembly test handlers
```

## Building

```bash
dotnet build MDator.slnx
dotnet test tests/MDator.Tests/MDator.Tests.csproj
```

## License

[MIT](LICENSE)
