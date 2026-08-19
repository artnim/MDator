using System.Runtime.CompilerServices;
using MDator.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VerifyXunit;

namespace MDator.Tests;

public static class VerifyModuleInit
{
  [ModuleInitializer]
  public static void Init() => VerifySourceGenerators.Initialize();
}

/// <summary>
/// Snapshot tests for the emitted <c>MDatorGenerated.g.cs</c>. Each test runs
/// the incremental generator over a small compilation and verifies the full
/// generated output against a committed snapshot in <c>Snapshots/</c>.
/// The generated code is also compiled to assert it is error-free.
/// </summary>
public sealed class GeneratorSnapshotTests
{
  [Fact]
  public Task Request_with_response() => VerifyGenerated("""
      using System.Threading;
      using System.Threading.Tasks;
      using MDator;

      namespace Snap;

      public record GetUser(int Id) : IRequest<string>;

      public sealed class GetUserHandler : IRequestHandler<GetUser, string>
      {
        public Task<string> Handle(GetUser request, CancellationToken ct) => Task.FromResult("u");
      }
      """);

  [Fact]
  public Task Void_request() => VerifyGenerated("""
      using System.Threading;
      using System.Threading.Tasks;
      using MDator;

      namespace Snap;

      public record Fire() : IRequest;

      public sealed class FireHandler : IRequestHandler<Fire>
      {
        public Task Handle(Fire request, CancellationToken ct) => Task.CompletedTask;
      }
      """);

  [Fact]
  public Task Stream_request() => VerifyGenerated("""
      using System.Collections.Generic;
      using System.Threading;
      using MDator;

      namespace Snap;

      public record Ticks(int Count) : IStreamRequest<int>;

      public sealed class TicksHandler : IStreamRequestHandler<Ticks, int>
      {
        public async IAsyncEnumerable<int> Handle(Ticks request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
          for (var i = 0; i < request.Count; i++) yield return i;
          await System.Threading.Tasks.Task.CompletedTask;
        }
      }
      """);

  [Fact]
  public Task Notification_with_two_handlers() => VerifyGenerated("""
      using System.Threading;
      using System.Threading.Tasks;
      using MDator;

      namespace Snap;

      public record UserCreated(int Id) : INotification;

      public sealed class SendWelcomeMail : INotificationHandler<UserCreated>
      {
        public Task Handle(UserCreated notification, CancellationToken ct) => Task.CompletedTask;
      }

      public sealed class AuditUserCreated : INotificationHandler<UserCreated>
      {
        public Task Handle(UserCreated notification, CancellationToken ct) => Task.CompletedTask;
      }
      """);

  [Fact]
  public Task Open_generic_behaviors_with_order() => VerifyGenerated("""
      using System.Threading;
      using System.Threading.Tasks;
      using MDator;

      [assembly: OpenBehavior(typeof(Snap.LoggingBehavior<,>), Order = 1)]
      [assembly: OpenBehavior(typeof(Snap.ValidationBehavior<,>), Order = 0)]

      namespace Snap;

      public record GetUser(int Id) : IRequest<string>;

      public sealed class GetUserHandler : IRequestHandler<GetUser, string>
      {
        public Task<string> Handle(GetUser request, CancellationToken ct) => Task.FromResult("u");
      }

      public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
          where TRequest : notnull
      {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct) => next();
      }

      public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
          where TRequest : notnull
      {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct) => next();
      }
      """);

  [Fact]
  public Task Closed_behavior() => VerifyGenerated("""
      using System.Threading;
      using System.Threading.Tasks;
      using MDator;

      namespace Snap;

      public record Compute(int X) : IRequest<int>;

      public sealed class ComputeHandler : IRequestHandler<Compute, int>
      {
        public Task<int> Handle(Compute request, CancellationToken ct) => Task.FromResult(request.X);
      }

      public sealed class DoubleItBehavior : IPipelineBehavior<Compute, int>
      {
        public async Task<int> Handle(Compute request, RequestHandlerDelegate<int> next, CancellationToken ct) => await next() * 2;
      }
      """);

  [Fact]
  public Task Pre_and_post_processors() => VerifyGenerated("""
      using System.Threading;
      using System.Threading.Tasks;
      using MDator;

      namespace Snap;

      public record Save(string Name) : IRequest<string>;

      public sealed class SaveHandler : IRequestHandler<Save, string>
      {
        public Task<string> Handle(Save request, CancellationToken ct) => Task.FromResult(request.Name);
      }

      public sealed class SavePre : IRequestPreProcessor<Save>
      {
        public Task Process(Save request, CancellationToken ct) => Task.CompletedTask;
      }

      public sealed class SavePost : IRequestPostProcessor<Save, string>
      {
        public Task Process(Save request, string response, CancellationToken ct) => Task.CompletedTask;
      }
      """);

  [Fact]
  public Task Exception_handler_and_action() => VerifyGenerated("""
      using System;
      using System.Threading;
      using System.Threading.Tasks;
      using MDator;

      namespace Snap;

      public record Risky() : IRequest<string>;

      public sealed class RiskyHandler : IRequestHandler<Risky, string>
      {
        public Task<string> Handle(Risky request, CancellationToken ct) => throw new InvalidOperationException();
      }

      public sealed class RiskyExceptionHandler : IRequestExceptionHandler<Risky, string, InvalidOperationException>
      {
        public Task Handle(Risky request, InvalidOperationException exception, RequestExceptionHandlerState<string> state, CancellationToken ct)
        {
          state.SetHandled("recovered");
          return Task.CompletedTask;
        }
      }

      public sealed class RiskyExceptionAction : IRequestExceptionAction<Risky, Exception>
      {
        public Task Execute(Risky request, Exception exception, CancellationToken ct) => Task.CompletedTask;
      }
      """);

  [Fact]
  public void No_handlers_emits_nothing()
  {
    var compilation = CreateCompilation("""
        namespace Snap;

        public sealed class NotAHandler
        {
        }
        """, "SnapshotTest");

    var driver = RunGenerator(compilation, out var output);
    AssertNoErrors(output);
    var result = driver.GetRunResult();
    Assert.Empty(result.Results.Single().GeneratedSources);
  }

  [Fact]
  public Task Cross_assembly_known_request()
  {
    // Stage 1: a "contrib" assembly whose generator run emits
    // [assembly: KnownRequest(typeof(...))] alongside its own mediator.
    var contrib = CreateCompilation("""
        using System.Threading;
        using System.Threading.Tasks;
        using MDator;

        namespace Contrib;

        public record Ping(string Message) : IRequest<string>;

        public sealed class PingHandler : IRequestHandler<Ping, string>
        {
          public Task<string> Handle(Ping request, CancellationToken ct) => Task.FromResult(request.Message);
        }
        """, "Contrib");
    RunGenerator(contrib, out var contribWithGenerated);
    AssertNoErrors(contribWithGenerated);

    using var contribImage = new MemoryStream();
    var emit = contribWithGenerated.Emit(contribImage);
    Assert.True(emit.Success);
    var contribReference = MetadataReference.CreateFromImage(contribImage.ToArray());

    // Stage 2: a consumer assembly with no handlers of its own — its mediator
    // must still get a compile-time switch arm for Contrib.Ping.
    var consumer = CreateCompilation("""
        namespace Consumer;

        public sealed class CompositionRoot
        {
        }
        """, "Consumer", [contribReference]);
    var driver = RunGenerator(consumer, out var consumerOutput);
    AssertNoErrors(consumerOutput);

    return Verifier.Verify(driver)
        .UseDirectory("Snapshots")
        .UseFileName(nameof(Cross_assembly_known_request));
  }

  private static Task VerifyGenerated(string source, [CallerMemberName] string testName = "")
  {
    var compilation = CreateCompilation(source, "SnapshotTest");
    var driver = RunGenerator(compilation, out var output);
    AssertNoErrors(output);
    return Verifier.Verify(driver)
        .UseDirectory("Snapshots")
        .UseFileName(testName);
  }

  private static GeneratorDriver RunGenerator(CSharpCompilation compilation, out Compilation outputCompilation)
  {
    AssertNoErrors(compilation);
    GeneratorDriver driver = CSharpGeneratorDriver.Create(new MDatorIncrementalGenerator());
    return driver.RunGeneratorsAndUpdateCompilation(compilation, out outputCompilation, out _);
  }

  private static void AssertNoErrors(Compilation compilation)
  {
    var errors = compilation.GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .ToArray();
    Assert.Empty(errors);
  }

  private static CSharpCompilation CreateCompilation(
      string source,
      string assemblyName,
      IEnumerable<MetadataReference>? extraReferences = null)
  {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);
    return CSharpCompilation.Create(
        assemblyName,
        [syntaxTree],
        References.Concat(extraReferences ?? []),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
  }

  /// <summary>
  /// Framework + MDator references. Unlike <see cref="NoOpShimAnalyzerTests"/>
  /// we must exclude the test assemblies themselves: MDator.Tests.CrossAssembly
  /// carries generated <c>[assembly: KnownRequest]</c> attributes that would
  /// otherwise leak cross-assembly switch arms into every snapshot.
  /// </summary>
  private static readonly MetadataReference[] References = BuildReferences();

  private static MetadataReference[] BuildReferences()
  {
    var trustedAssembliesPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator);
    return trustedAssembliesPaths
        .Where(p => !Path.GetFileNameWithoutExtension(p).StartsWith("MDator.Tests", StringComparison.Ordinal))
        .Select(p => MetadataReference.CreateFromFile(p))
        .Cast<MetadataReference>()
        .ToArray();
  }
}
