using System.Collections.Generic;
using System.Linq;

namespace MDator.SourceGenerator;

/// <summary>
/// Emits the whole generated MDator surface for a consuming assembly:
/// <list type="bullet">
///   <item>The <c>MDatorGeneratedRegistration</c> static class with a
///     <c>[ModuleInitializer]</c> that registers every handler with DI.</item>
///   <item>The <c>GeneratedMediator</c> sealed implementation of
///     <see cref="IMediator"/> with compile-time dispatch for every request,
///     stream and notification the generator discovered.</item>
/// </list>
/// </summary>
internal static class MediatorEmitter
{
  /// <summary>
  /// Returns handlers sorted so that derived message types come before their
  /// base types. This prevents CS8120 (unreachable switch case) when a request
  /// type inherits from another request type.
  /// </summary>
  private static List<HandlerInfo> SortedByTypeHierarchy(IEnumerable<HandlerInfo> handlers) =>
      handlers.OrderByDescending(h => h.MessageTypeDepth).ToList();

  /// <summary>
  /// Builds a combined, type-hierarchy-sorted list of switch entries from both
  /// same-assembly handlers and cross-assembly request infos. Derived types come
  /// first to prevent CS8120 (unreachable switch case).
  /// </summary>
  private static List<(TypeRef MessageType, TypeRef? ResponseType, bool IsCrossAssembly)> CombinedSwitchEntries(
      IEnumerable<HandlerInfo> sameAssembly,
      List<CrossAssemblyRequestInfo> crossAssembly)
  {
    var entries = new List<(TypeRef MessageType, TypeRef? ResponseType, bool IsCrossAssembly, int Depth)>();
    foreach (var h in sameAssembly)
      entries.Add((h.MessageType, h.ResponseType, false, h.MessageTypeDepth));
    foreach (var x in crossAssembly)
      entries.Add((x.MessageType, x.ResponseType, true, x.MessageTypeDepth));
    return entries
        .OrderByDescending(e => e.Depth)
        .Select(e => (e.MessageType, e.ResponseType, e.IsCrossAssembly))
        .ToList();
  }

  public static string Emit(PipelineModel model)
  {
    // Group handlers by kind for easier lookup.
    var requestHandlers = model.Handlers
        .Where(h => h.Kind == HandlerKind.RequestWithResponse && !h.HandlerIsOpenGeneric)
        .GroupBy(h => h.MessageType.GlobalName)
        .ToDictionary(g => g.Key, g => g.First());

    var voidHandlers = model.Handlers
        .Where(h => h.Kind == HandlerKind.RequestVoid && !h.HandlerIsOpenGeneric)
        .GroupBy(h => h.MessageType.GlobalName)
        .ToDictionary(g => g.Key, g => g.First());

    var streamHandlers = model.Handlers
        .Where(h => h.Kind == HandlerKind.Stream && !h.HandlerIsOpenGeneric)
        .GroupBy(h => h.MessageType.GlobalName)
        .ToDictionary(g => g.Key, g => g.First());

    var notificationHandlers = model.Handlers
        .Where(h => h.Kind == HandlerKind.Notification && !h.HandlerIsOpenGeneric)
        .GroupBy(h => h.MessageType.GlobalName)
        .ToDictionary(g => g.Key, g => g.ToList());

    var preProcessors = model.Handlers
        .Where(h => h.Kind == HandlerKind.PreProcessor)
        .GroupBy(h => h.MessageType.GlobalName)
        .ToDictionary(g => g.Key, g => g.ToList());

    var postProcessors = model.Handlers
        .Where(h => h.Kind == HandlerKind.PostProcessor)
        .GroupBy(h => (h.MessageType.GlobalName, h.ResponseType!.GlobalName))
        .ToDictionary(g => g.Key, g => g.ToList());

    var exceptionHandlers = model.Handlers
        .Where(h => h.Kind == HandlerKind.ExceptionHandler)
        .GroupBy(h => (h.MessageType.GlobalName, h.ResponseType!.GlobalName))
        .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.ExceptionDepth).ToList());

    var exceptionActions = model.Handlers
        .Where(h => h.Kind == HandlerKind.ExceptionAction)
        .GroupBy(h => h.MessageType.GlobalName)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.ExceptionDepth).ToList());

    // Open behaviors, sorted by declared order. Closed-at-compile-time
    // pipeline behaviors (not open generic) are handled as runtime-resolved
    // services via the enumeration fallback — fusing them explicitly would
    // require key-by-(Req,Resp) which adds little value over MSDI's enumerator.
    var openRequestBehaviors = model.Behaviors
        .Where(b => b.Kind == BehaviorKind.Request && b.IsOpenGeneric)
        .OrderBy(b => b.Order).ThenBy(b => b.BehaviorType.GlobalName)
        .ToList();

    var openStreamBehaviors = model.Behaviors
        .Where(b => b.Kind == BehaviorKind.Stream && b.IsOpenGeneric)
        .OrderBy(b => b.Order).ThenBy(b => b.BehaviorType.GlobalName)
        .ToList();

    // Cross-assembly requests: filter out any already handled same-assembly,
    // then split by kind.
    var sameAssemblyKeys = new HashSet<string>(
        requestHandlers.Keys
            .Concat(voidHandlers.Keys)
            .Concat(streamHandlers.Keys)
            .Concat(notificationHandlers.Keys));

    var xasmRequests = model.CrossAssemblyRequests
        .Where(x => !sameAssemblyKeys.Contains(x.MessageType.GlobalName))
        .ToList();

    var xasmRequestWithResp = xasmRequests
        .Where(x => x.Kind == HandlerKind.RequestWithResponse).ToList();
    var xasmVoid = xasmRequests
        .Where(x => x.Kind == HandlerKind.RequestVoid).ToList();
    var xasmStream = xasmRequests
        .Where(x => x.Kind == HandlerKind.Stream).ToList();
    var xasmNotification = xasmRequests
        .Where(x => x.Kind == HandlerKind.Notification).ToList();

    var w = new CodeWriter();
    EmitHeader(w);
    EmitKnownRequestAttributes(w, model.Handlers);
    EmitNamespaceOpen(w, model.RootNamespace);

    EmitRegistration(w, model, requestHandlers, voidHandlers, streamHandlers,
        notificationHandlers, preProcessors, postProcessors,
        exceptionHandlers, exceptionActions);

    w.Line();

    EmitMediator(w, requestHandlers, voidHandlers, streamHandlers,
        notificationHandlers, preProcessors, postProcessors,
        exceptionHandlers, exceptionActions,
        openRequestBehaviors, openStreamBehaviors,
        xasmRequestWithResp, xasmVoid, xasmStream, xasmNotification);

    EmitNamespaceClose(w);
    return w.ToString();
  }

  private static void EmitHeader(CodeWriter w)
  {
    w.Line("// <auto-generated />");
    w.Line("#nullable enable");
    w.Line("#pragma warning disable CS1591 // missing XML doc on generated members");
    w.Line();
    w.Line("using System;");
    w.Line("using System.Collections.Generic;");
    w.Line("using System.Runtime.CompilerServices;");
    w.Line("using System.Threading;");
    w.Line("using System.Threading.Tasks;");
    w.Line("using Microsoft.Extensions.DependencyInjection;");
    w.Line("using MDator;");
    w.Line();
  }

  /// <summary>
  /// Emits <c>[assembly: KnownRequest(typeof(...))]</c> for every unique message
  /// type in this assembly so that downstream consuming assemblies can include
  /// them in their compile-time dispatch switches.
  /// </summary>
  private static void EmitKnownRequestAttributes(CodeWriter w, EquatableArray<HandlerInfo> handlers)
  {
    var seen = new HashSet<string>();
    foreach (var h in handlers)
    {
      // Only primary handler kinds — processors/exception handlers don't
      // define new dispatchable message types.
      if (h.Kind is not (HandlerKind.RequestWithResponse or HandlerKind.RequestVoid
          or HandlerKind.Stream or HandlerKind.Notification)) continue;
      if (h.HandlerIsOpenGeneric) continue; // typeof() can't reference open generics
      if (!seen.Add(h.MessageType.GlobalName)) continue;
      w.Line($"[assembly: global::MDator.KnownRequestAttribute(typeof({h.MessageType.GlobalName}))]");
    }
    if (seen.Count > 0) w.Line();
  }

  private static string NsIdentifier(string rootNamespace)
  {
    var sb = new System.Text.StringBuilder();
    foreach (var c in rootNamespace)
    {
      sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
    }
    return sb.Length == 0 ? "GeneratedMDator" : sb.ToString();
  }

  private static void EmitNamespaceOpen(CodeWriter w, string rootNamespace)
  {
    w.Line($"namespace MDator.Generated.{NsIdentifier(rootNamespace)}");
    w.OpenBrace();
  }

  private static void EmitNamespaceClose(CodeWriter w) => w.CloseBrace();

  private static void EmitRegistration(
      CodeWriter w,
      PipelineModel model,
      Dictionary<string, HandlerInfo> requestHandlers,
      Dictionary<string, HandlerInfo> voidHandlers,
      Dictionary<string, HandlerInfo> streamHandlers,
      Dictionary<string, List<HandlerInfo>> notificationHandlers,
      Dictionary<string, List<HandlerInfo>> preProcessors,
      Dictionary<(string, string), List<HandlerInfo>> postProcessors,
      Dictionary<(string, string), List<HandlerInfo>> exceptionHandlers,
      Dictionary<string, List<HandlerInfo>> exceptionActions)
  {
    w.Line("[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = \"Code Generated by MDator Source Generator\")]");
    w.Line("internal static class MDatorGeneratedRegistration");
    w.OpenBrace();
    w.Line("[ModuleInitializer]");
    w.Line("internal static void Init()");
    w.OpenBrace();
    w.Line("global::MDator.MDatorGeneratedHook.Registrations.Add(Register);");
    w.CloseBrace();
    w.Line();

    w.Line("private static void Register(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services, global::MDator.MDatorConfiguration cfg)");
    w.OpenBrace();
    w.Line("var lt = cfg.Lifetime;");
    w.Line("// IMediator registered once per consuming assembly set; TryAdd to avoid duplicates when");
    w.Line("// multiple generated registrations run in the same composition root.");
    w.Line("global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAdd(services, new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.IMediator), typeof(GeneratedMediator), lt));");
    w.Line("global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAdd(services, new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.ISender), static sp => sp.GetRequiredService<global::MDator.IMediator>(), lt));");
    w.Line("global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAdd(services, new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.IPublisher), static sp => sp.GetRequiredService<global::MDator.IMediator>(), lt));");
    w.Line();

    // Request handlers with response
    foreach (var h in requestHandlers.Select(kv => kv.Value))
    {
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof({h.HandlerType.GlobalName}), typeof({h.HandlerType.GlobalName}), lt));");
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.IRequestHandler<{h.MessageType.GlobalName}, {h.ResponseType!.GlobalName}>), static sp => sp.GetRequiredService<{h.HandlerType.GlobalName}>(), lt));");
    }
    // Void handlers
    foreach (var h in voidHandlers.Select(kv => kv.Value))
    {
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof({h.HandlerType.GlobalName}), typeof({h.HandlerType.GlobalName}), lt));");
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.IRequestHandler<{h.MessageType.GlobalName}>), static sp => sp.GetRequiredService<{h.HandlerType.GlobalName}>(), lt));");
    }
    // Stream handlers
    foreach (var h in streamHandlers.Select(kv => kv.Value))
    {
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof({h.HandlerType.GlobalName}), typeof({h.HandlerType.GlobalName}), lt));");
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.IStreamRequestHandler<{h.MessageType.GlobalName}, {h.ResponseType!.GlobalName}>), static sp => sp.GetRequiredService<{h.HandlerType.GlobalName}>(), lt));");
    }
    // Notification handlers (multiple per type, AddEnumerable-style via raw Add)
    foreach (var h in notificationHandlers.SelectMany(kv => kv.Value))
    {
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.INotificationHandler<{h.MessageType.GlobalName}>), typeof({h.HandlerType.GlobalName}), lt));");
    }
    // Pre-processors
    foreach (var h in preProcessors.SelectMany(kv => kv.Value))
    {
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.IRequestPreProcessor<{h.MessageType.GlobalName}>), typeof({h.HandlerType.GlobalName}), lt));");
    }
    // Post-processors
    foreach (var h in postProcessors.SelectMany(kv => kv.Value))
    {
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.IRequestPostProcessor<{h.MessageType.GlobalName}, {h.ResponseType!.GlobalName}>), typeof({h.HandlerType.GlobalName}), lt));");
    }
    // Exception handlers
    foreach (var h in exceptionHandlers.SelectMany(kv => kv.Value))
    {
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.IRequestExceptionHandler<{h.MessageType.GlobalName}, {h.ResponseType!.GlobalName}, {h.ExceptionType!.GlobalName}>), typeof({h.HandlerType.GlobalName}), lt));");
    }
    // Exception actions
    foreach (var h in exceptionActions.SelectMany(kv => kv.Value))
    {
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::MDator.IRequestExceptionAction<{h.MessageType.GlobalName}, {h.ExceptionType!.GlobalName}>), typeof({h.HandlerType.GlobalName}), lt));");
    }
    // Closed pipeline behaviors discovered by class scan. Registered under the
    // framework interface so the runtime enumeration fallback picks them up.
    foreach (var b in model.Behaviors)
    {
      if (b.IsOpenGeneric) continue;
      if (b.ClosedRequestType is null || b.ClosedResponseType is null) continue;
      var iface = b.Kind == BehaviorKind.Request
          ? $"global::MDator.IPipelineBehavior<{b.ClosedRequestType.GlobalName}, {b.ClosedResponseType.GlobalName}>"
          : $"global::MDator.IStreamPipelineBehavior<{b.ClosedRequestType.GlobalName}, {b.ClosedResponseType.GlobalName}>";
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof({iface}), typeof({b.BehaviorType.GlobalName}), lt));");
    }

    // Open behaviors — self-register under their own open-generic type only.
    // The fused code path resolves them by implementation type. We intentionally
    // do NOT register them under IPipelineBehavior<,> because the generated
    // pipeline ALSO enumerates that interface for runtime-added behaviors; a
    // double registration would cause every fused behavior to execute twice.
    // Users who want a behavior in both paths can declare it via the attribute
    // (fused path) OR via services.AddTransient(typeof(IPipelineBehavior<,>), ...)
    // in configure (runtime path), but not both.
    foreach (var b in model.Behaviors)
    {
      if (!b.IsOpenGeneric) continue;
      w.Line($"services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof({b.BehaviorType.GlobalNameUnbound}), typeof({b.BehaviorType.GlobalNameUnbound}), lt));");
      w.Line($"cfg.RegisterOpenBehavior(typeof({b.BehaviorType.GlobalNameUnbound}), {b.Order});");
    }

    w.CloseBrace();
    w.CloseBrace();
  }

  private static void EmitMediator(
      CodeWriter w,
      Dictionary<string, HandlerInfo> requestHandlers,
      Dictionary<string, HandlerInfo> voidHandlers,
      Dictionary<string, HandlerInfo> streamHandlers,
      Dictionary<string, List<HandlerInfo>> notificationHandlers,
      Dictionary<string, List<HandlerInfo>> preProcessors,
      Dictionary<(string, string), List<HandlerInfo>> postProcessors,
      Dictionary<(string, string), List<HandlerInfo>> exceptionHandlers,
      Dictionary<string, List<HandlerInfo>> exceptionActions,
      List<BehaviorInfo> openRequestBehaviors,
      List<BehaviorInfo> openStreamBehaviors,
      List<CrossAssemblyRequestInfo> xasmRequestWithResp,
      List<CrossAssemblyRequestInfo> xasmVoid,
      List<CrossAssemblyRequestInfo> xasmStream,
      List<CrossAssemblyRequestInfo> xasmNotification)
  {
    w.Line("internal sealed class GeneratedMediator : global::MDator.IMediator");
    w.OpenBrace();
    w.Line("private readonly global::System.IServiceProvider _sp;");
    w.Line("private readonly global::MDator.MDatorConfiguration _cfg;");
    w.Line("private readonly global::MDator.INotificationPublisher _publisher;");
    w.Line();
    w.Line("public GeneratedMediator(global::System.IServiceProvider sp, global::MDator.MDatorConfiguration cfg, global::MDator.INotificationPublisher publisher)");
    w.OpenBrace();
    w.Line("_sp = sp;");
    w.Line("_cfg = cfg;");
    w.Line("_publisher = publisher;");
    w.CloseBrace();
    w.Line();

    EmitSendWithResponse(w, requestHandlers, xasmRequestWithResp);
    EmitSendVoid(w, voidHandlers, xasmVoid);
    EmitSendObject(w, requestHandlers, voidHandlers, xasmRequestWithResp, xasmVoid);
    EmitCreateStream(w, streamHandlers, xasmStream);
    EmitCreateStreamObject(w, streamHandlers, xasmStream);
    EmitPublish(w, notificationHandlers, xasmNotification);
    EmitPublishObject(w, notificationHandlers, xasmNotification);

    // Per-request pipeline composers — same-assembly
    foreach (var kv in requestHandlers)
    {
      EmitRequestPipeline(w, kv.Value, preProcessors, postProcessors,
          exceptionHandlers, exceptionActions, openRequestBehaviors);
    }
    foreach (var kv in voidHandlers)
    {
      EmitVoidRequestPipeline(w, kv.Value, preProcessors, openRequestBehaviors);
    }
    foreach (var kv in streamHandlers)
    {
      EmitStreamPipeline(w, kv.Value, openStreamBehaviors);
    }
    foreach (var kv in notificationHandlers)
    {
      EmitNotificationPublish(w, kv.Key, kv.Value);
    }

    // Per-request pipeline composers — cross-assembly
    foreach (var x in xasmRequestWithResp)
    {
      EmitCrossAssemblyRequestPipeline(w, x.MessageType, x.ResponseType!, openRequestBehaviors);
    }
    foreach (var x in xasmVoid)
    {
      EmitCrossAssemblyVoidPipeline(w, x.MessageType, openRequestBehaviors);
    }
    foreach (var x in xasmStream)
    {
      EmitCrossAssemblyStreamPipeline(w, x.MessageType, x.ResponseType!, openStreamBehaviors);
    }
    foreach (var x in xasmNotification)
    {
      EmitCrossAssemblyNotificationPublish(w, x.MessageType);
    }

    w.CloseBrace();
  }

  private static void EmitSendWithResponse(CodeWriter w, Dictionary<string, HandlerInfo> requestHandlers, List<CrossAssemblyRequestInfo> xasm)
  {
    w.Line("public global::System.Threading.Tasks.Task<TResponse> Send<TResponse>(global::MDator.IRequest<TResponse> request, global::System.Threading.CancellationToken cancellationToken = default)");
    w.OpenBrace();
    w.Line("switch (request)");
    w.OpenBrace();
    foreach (var (msg, _, _) in CombinedSwitchEntries(requestHandlers.Values, xasm))
    {
      w.Line($"case {msg.GlobalName} __req_{msg.Identifier}:");
      w.Indent();
      w.Line($"return (global::System.Threading.Tasks.Task<TResponse>)(object)SendCore_{msg.Identifier}(__req_{msg.Identifier}, cancellationToken);");
      w.Dedent();
    }
    w.Line("default:");
    w.Indent();
    w.Line("return SendUnknown<TResponse>(request, cancellationToken);");
    w.Dedent();
    w.CloseBrace();
    w.CloseBrace();
    w.Line();

    w.Line("private global::System.Threading.Tasks.Task<TResponse> SendUnknown<TResponse>(global::MDator.IRequest<TResponse> request, global::System.Threading.CancellationToken cancellationToken)");
    w.OpenBrace();
    w.Line("return global::MDator.RuntimeDispatch.SendFallback<TResponse>(_sp, _cfg, request, cancellationToken);");
    w.CloseBrace();
    w.Line();
  }

  private static void EmitSendVoid(CodeWriter w, Dictionary<string, HandlerInfo> voidHandlers, List<CrossAssemblyRequestInfo> xasm)
  {
    w.Line("public global::System.Threading.Tasks.Task Send<TRequest>(TRequest request, global::System.Threading.CancellationToken cancellationToken = default) where TRequest : global::MDator.IRequest");
    w.OpenBrace();
    w.Line("switch (request)");
    w.OpenBrace();
    foreach (var (msg, _, _) in CombinedSwitchEntries(voidHandlers.Values, xasm))
    {
      w.Line($"case {msg.GlobalName} __req_{msg.Identifier}:");
      w.Indent();
      w.Line($"return SendVoidCore_{msg.Identifier}(__req_{msg.Identifier}, cancellationToken);");
      w.Dedent();
    }
    w.Line("default:");
    w.Indent();
    w.Line("return global::MDator.RuntimeDispatch.SendVoidFallback(_sp, _cfg, request, cancellationToken);");
    w.Dedent();
    w.CloseBrace();
    w.CloseBrace();
    w.Line();
  }

  private static void EmitSendObject(
      CodeWriter w,
      Dictionary<string, HandlerInfo> requestHandlers,
      Dictionary<string, HandlerInfo> voidHandlers,
      List<CrossAssemblyRequestInfo> xasmReq,
      List<CrossAssemblyRequestInfo> xasmVoid)
  {
    w.Line("public async global::System.Threading.Tasks.Task<object?> Send(object request, global::System.Threading.CancellationToken cancellationToken = default)");
    w.OpenBrace();
    w.Line("switch (request)");
    w.OpenBrace();
    foreach (var (msg, _, _) in CombinedSwitchEntries(requestHandlers.Values, xasmReq))
    {
      w.Line($"case {msg.GlobalName} __req_{msg.Identifier}:");
      w.Indent();
      w.Line($"return await SendCore_{msg.Identifier}(__req_{msg.Identifier}, cancellationToken).ConfigureAwait(false);");
      w.Dedent();
    }
    foreach (var (msg, _, _) in CombinedSwitchEntries(voidHandlers.Values, xasmVoid))
    {
      w.Line($"case {msg.GlobalName} __req_{msg.Identifier}:");
      w.Indent();
      w.Line($"await SendVoidCore_{msg.Identifier}(__req_{msg.Identifier}, cancellationToken).ConfigureAwait(false); return null;");
      w.Dedent();
    }
    w.Line("default:");
    w.Indent();
    w.Line("return await global::MDator.RuntimeDispatch.SendObjectFallback(_sp, _cfg, request, cancellationToken).ConfigureAwait(false);");
    w.Dedent();
    w.CloseBrace();
    w.CloseBrace();
    w.Line();
  }

  private static void EmitCreateStream(CodeWriter w, Dictionary<string, HandlerInfo> streamHandlers, List<CrossAssemblyRequestInfo> xasm)
  {
    w.Line("public global::System.Collections.Generic.IAsyncEnumerable<TResponse> CreateStream<TResponse>(global::MDator.IStreamRequest<TResponse> request, global::System.Threading.CancellationToken cancellationToken = default)");
    w.OpenBrace();
    w.Line("switch (request)");
    w.OpenBrace();
    foreach (var (msg, _, _) in CombinedSwitchEntries(streamHandlers.Values, xasm))
    {
      w.Line($"case {msg.GlobalName} __req_{msg.Identifier}:");
      w.Indent();
      w.Line($"return (global::System.Collections.Generic.IAsyncEnumerable<TResponse>)(object)StreamCore_{msg.Identifier}(__req_{msg.Identifier}, cancellationToken);");
      w.Dedent();
    }
    w.Line("default:");
    w.Indent();
    w.Line("return global::MDator.RuntimeDispatch.StreamFallback<TResponse>(_sp, _cfg, request, cancellationToken);");
    w.Dedent();
    w.CloseBrace();
    w.CloseBrace();
    w.Line();
  }

  private static void EmitCreateStreamObject(CodeWriter w, Dictionary<string, HandlerInfo> streamHandlers, List<CrossAssemblyRequestInfo> xasm)
  {
    var allEntries = CombinedSwitchEntries(streamHandlers.Values, xasm);

    // When there are no stream handlers at all, delegate entirely to the
    // runtime fallback. No async/yield needed — just return the fallback.
    if (allEntries.Count == 0)
    {
      w.Line("public global::System.Collections.Generic.IAsyncEnumerable<object?> CreateStream(object request, global::System.Threading.CancellationToken cancellationToken = default)");
      w.OpenBrace();
      w.Line("return global::MDator.RuntimeDispatch.StreamObjectFallback(_sp, _cfg, request, cancellationToken);");
      w.CloseBrace();
      w.Line();
      return;
    }

    w.Line("public async global::System.Collections.Generic.IAsyncEnumerable<object?> CreateStream(object request, [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken cancellationToken = default)");
    w.OpenBrace();
    foreach (var (msg, _, _) in allEntries)
    {
      w.Line($"if (request is {msg.GlobalName} __req_{msg.Identifier})");
      w.OpenBrace();
      w.Line($"await foreach (var __item in StreamCore_{msg.Identifier}(__req_{msg.Identifier}, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))");
      w.OpenBrace();
      w.Line("yield return __item;");
      w.CloseBrace();
      w.Line("yield break;");
      w.CloseBrace();
    }
    // Fallback: resolve truly unknown stream handlers from DI.
    w.Line("await foreach (var __item in global::MDator.RuntimeDispatch.StreamObjectFallback(_sp, _cfg, request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))");
    w.OpenBrace();
    w.Line("yield return __item;");
    w.CloseBrace();
    w.CloseBrace();
    w.Line();
  }

  private static void EmitPublish(CodeWriter w, Dictionary<string, List<HandlerInfo>> notificationHandlers, List<CrossAssemblyRequestInfo> xasm)
  {
    w.Line("public global::System.Threading.Tasks.Task Publish<TNotification>(TNotification notification, global::System.Threading.CancellationToken cancellationToken = default) where TNotification : global::MDator.INotification");
    w.OpenBrace();
    w.Line("switch (notification)");
    w.OpenBrace();
    foreach (HandlerInfo? first in notificationHandlers.Select(kv => kv.Value[0]))
    {
      w.Line($"case {first.MessageType.GlobalName} __n_{first.MessageType.Identifier}:");
      w.Indent();
      w.Line($"return PublishCore_{first.MessageType.Identifier}(__n_{first.MessageType.Identifier}, cancellationToken);");
      w.Dedent();
    }
    foreach (var x in xasm)
    {
      w.Line($"case {x.MessageType.GlobalName} __n_{x.MessageType.Identifier}:");
      w.Indent();
      w.Line($"return PublishCore_{x.MessageType.Identifier}(__n_{x.MessageType.Identifier}, cancellationToken);");
      w.Dedent();
    }
    w.Line("default:");
    w.Indent();
    w.Line("return global::MDator.RuntimeDispatch.PublishFallback(_sp, _publisher, notification, cancellationToken);");
    w.Dedent();
    w.CloseBrace();
    w.CloseBrace();
    w.Line();
  }

  private static void EmitPublishObject(CodeWriter w, Dictionary<string, List<HandlerInfo>> notificationHandlers, List<CrossAssemblyRequestInfo> xasm)
  {
    w.Line("public global::System.Threading.Tasks.Task Publish(object notification, global::System.Threading.CancellationToken cancellationToken = default)");
    w.OpenBrace();
    w.Line("if (notification is null) throw new global::System.ArgumentNullException(nameof(notification));");
    w.Line("if (notification is not global::MDator.INotification __n_typed) throw new global::System.ArgumentException(\"MDator: Publish requires an INotification instance.\", nameof(notification));");
    w.Line("switch (notification)");
    w.OpenBrace();
    foreach (HandlerInfo? first in notificationHandlers.Select(kv => kv.Value[0]))
    {
      w.Line($"case {first.MessageType.GlobalName} __n_{first.MessageType.Identifier}:");
      w.Indent();
      w.Line($"return PublishCore_{first.MessageType.Identifier}(__n_{first.MessageType.Identifier}, cancellationToken);");
      w.Dedent();
    }
    foreach (var x in xasm)
    {
      w.Line($"case {x.MessageType.GlobalName} __n_{x.MessageType.Identifier}:");
      w.Indent();
      w.Line($"return PublishCore_{x.MessageType.Identifier}(__n_{x.MessageType.Identifier}, cancellationToken);");
      w.Dedent();
    }
    w.Line("default:");
    w.Indent();
    w.Line("return global::MDator.RuntimeDispatch.PublishFallback(_sp, _publisher, __n_typed, cancellationToken);");
    w.Dedent();
    w.CloseBrace();
    w.CloseBrace();
    w.Line();
  }

  private static void EmitRequestPipeline(
      CodeWriter w,
      HandlerInfo handler,
      Dictionary<string, List<HandlerInfo>> preProcessors,
      Dictionary<(string, string), List<HandlerInfo>> postProcessors,
      Dictionary<(string, string), List<HandlerInfo>> exceptionHandlers,
      Dictionary<string, List<HandlerInfo>> exceptionActions,
      List<BehaviorInfo> openRequestBehaviors)
  {
    var req = handler.MessageType;
    var resp = handler.ResponseType!;
    var reqT = req.GlobalName;
    var respT = resp.GlobalName;

    preProcessors.TryGetValue(reqT, out var pres);
    postProcessors.TryGetValue((reqT, respT), out var posts);
    exceptionHandlers.TryGetValue((reqT, respT), out var exHandlers);
    exceptionActions.TryGetValue(reqT, out var exActions);

    w.Line($"private async global::System.Threading.Tasks.Task<{respT}> SendCore_{req.Identifier}({reqT} request, global::System.Threading.CancellationToken ct)");
    w.OpenBrace();
    w.Line($"var handler = _sp.GetRequiredService<global::MDator.IRequestHandler<{reqT}, {respT}>>();");
    if (pres is { Count: > 0 })
      w.Line($"var __pre = _sp.GetServices<global::MDator.IRequestPreProcessor<{reqT}>>();");
    if (posts is { Count: > 0 })
      w.Line($"var __post = _sp.GetServices<global::MDator.IRequestPostProcessor<{reqT}, {respT}>>();");
    w.Line();

    // Core delegate (innermost): pre → handler → post. A behavior may pass its
    // own token to next(); default keeps the ambient one (MediatR semantics).
    w.Line($"global::MDator.RequestHandlerDelegate<{respT}> next = async __t =>");
    w.OpenBrace();
    w.Line("var __ct = __t == default ? ct : __t;");
    if (pres is { Count: > 0 })
    {
      w.Line("foreach (var __p in __pre) await __p.Process(request, __ct).ConfigureAwait(false);");
    }
    w.Line("var __resp = await handler.Handle(request, __ct).ConfigureAwait(false);");
    if (posts is { Count: > 0 })
    {
      w.Line("foreach (var __p in __post) await __p.Process(request, __resp, __ct).ConfigureAwait(false);");
    }
    w.Line("return __resp;");
    w.CloseBraceWithSemicolon();
    w.Line();

    // Fused open behaviors. We resolve each as its closed type, outermost last so
    // that when we invoke `next` at the end, the outermost is called first.
    // Resolve in reverse declared order so declared-earlier = outermost.
    if (openRequestBehaviors.Count > 0)
    {
      var i = 0;
      foreach (var b in ((IEnumerable<BehaviorInfo>)openRequestBehaviors).Reverse())
      {
        var bType = b.BehaviorType.GlobalNameWithoutGenerics;
        w.Line($"var __b_{i} = _sp.GetRequiredService<{bType}<{reqT}, {respT}>>();");
        w.Line("{ var __prev = next; next = __t => __b_" + i + ".Handle(request, __prev, __t == default ? ct : __t); }");
        i++;
      }
    }
    w.Line();

    // Runtime-added behaviors (skipped under FuseOnly). Open behaviors declared
    // via [assembly: OpenBehavior] are NOT registered under IPipelineBehavior<,>
    // (see RegistrationEmitter), so this enumeration only sees behaviors the
    // user registered explicitly at runtime — no dedupe needed.
    w.Line("if (!_cfg.FuseOnly)");
    w.OpenBrace();
    w.Line($"foreach (var __rb in _sp.GetServices<global::MDator.IPipelineBehavior<{reqT}, {respT}>>())");
    w.OpenBrace();
    w.Line("{ var __prev2 = next; next = __t => __rb.Handle(request, __prev2, __t == default ? ct : __t); }");
    w.CloseBrace();
    w.CloseBrace();
    w.Line();

    // Exception wrap.
    if (exHandlers is { Count: > 0 } || exActions is { Count: > 0 })
    {
      w.Line("try");
      w.OpenBrace();
      w.Line("return await next().ConfigureAwait(false);");
      w.CloseBrace();
      w.Line("catch (global::System.Exception __ex)");
      w.OpenBrace();
      w.Line($"var __state = new global::MDator.RequestExceptionHandlerState<{respT}>();");
      if (exHandlers is { Count: > 0 })
      {
        foreach (var h in exHandlers)
        {
          var exT = h.ExceptionType!.GlobalName;
          w.Line($"if (__ex is {exT} __ex_{h.ExceptionType.Identifier} && !__state.Handled)");
          w.OpenBrace();
          w.Line($"foreach (var __h in _sp.GetServices<global::MDator.IRequestExceptionHandler<{reqT}, {respT}, {exT}>>())");
          w.OpenBrace();
          w.Line($"await __h.Handle(request, __ex_{h.ExceptionType.Identifier}, __state, ct).ConfigureAwait(false);");
          w.Line("if (__state.Handled) break;");
          w.CloseBrace();
          w.CloseBrace();
        }
      }
      if (exActions is { Count: > 0 })
      {
        foreach (var h in exActions)
        {
          var exT = h.ExceptionType!.GlobalName;
          w.Line($"if (__ex is {exT} __exa_{h.ExceptionType.Identifier})");
          w.OpenBrace();
          w.Line($"foreach (var __a in _sp.GetServices<global::MDator.IRequestExceptionAction<{reqT}, {exT}>>())");
          w.OpenBrace();
          w.Line($"await __a.Execute(request, __exa_{h.ExceptionType.Identifier}, ct).ConfigureAwait(false);");
          w.CloseBrace();
          w.CloseBrace();
        }
      }
      w.Line("if (__state.Handled) return __state.Response!;");
      w.Line("throw;");
      w.CloseBrace();
    }
    else
    {
      w.Line("return await next().ConfigureAwait(false);");
    }

    w.CloseBrace();
    w.Line();
  }

  private static void EmitVoidRequestPipeline(
      CodeWriter w,
      HandlerInfo handler,
      Dictionary<string, List<HandlerInfo>> preProcessors,
      List<BehaviorInfo> openRequestBehaviors)
  {
    var req = handler.MessageType;
    var reqT = req.GlobalName;
    preProcessors.TryGetValue(reqT, out var pres);

    // Void handlers are modeled as IRequestHandler<TRequest, Unit> internally so the
    // pipeline machinery is uniform. We synthesize a Unit-returning adapter so the
    // user's IRequestHandler<TRequest> can slot in.
    w.Line($"private async global::System.Threading.Tasks.Task SendVoidCore_{req.Identifier}({reqT} request, global::System.Threading.CancellationToken ct)");
    w.OpenBrace();
    w.Line($"var handler = _sp.GetRequiredService<global::MDator.IRequestHandler<{reqT}>>();");
    if (pres is { Count: > 0 })
      w.Line($"var __pre = _sp.GetServices<global::MDator.IRequestPreProcessor<{reqT}>>();");
    w.Line();

    w.Line("global::MDator.RequestHandlerDelegate<global::MDator.Unit> next = async __t =>");
    w.OpenBrace();
    w.Line("var __ct = __t == default ? ct : __t;");
    if (pres is { Count: > 0 })
      w.Line("foreach (var __p in __pre) await __p.Process(request, __ct).ConfigureAwait(false);");
    w.Line("await handler.Handle(request, __ct).ConfigureAwait(false);");
    w.Line("return global::MDator.Unit.Value;");
    w.CloseBraceWithSemicolon();
    w.Line();

    if (openRequestBehaviors.Count > 0)
    {
      var i = 0;
      foreach (var b in ((IEnumerable<BehaviorInfo>)openRequestBehaviors).Reverse())
      {
        var bType = b.BehaviorType.GlobalNameWithoutGenerics;
        w.Line($"var __b_{i} = _sp.GetRequiredService<{bType}<{reqT}, global::MDator.Unit>>();");
        w.Line("{ var __prev = next; next = __t => __b_" + i + ".Handle(request, __prev, __t == default ? ct : __t); }");
        i++;
      }
    }

    w.Line("if (!_cfg.FuseOnly)");
    w.OpenBrace();
    w.Line($"foreach (var __rb in _sp.GetServices<global::MDator.IPipelineBehavior<{reqT}, global::MDator.Unit>>())");
    w.OpenBrace();
    w.Line("{ var __prev2 = next; next = __t => __rb.Handle(request, __prev2, __t == default ? ct : __t); }");
    w.CloseBrace();
    w.CloseBrace();
    w.Line();

    w.Line("await next().ConfigureAwait(false);");
    w.CloseBrace();
    w.Line();
  }

  private static void EmitStreamPipeline(CodeWriter w, HandlerInfo handler, List<BehaviorInfo> openStreamBehaviors)
  {
    var req = handler.MessageType;
    var resp = handler.ResponseType!;
    var reqT = req.GlobalName;
    var respT = resp.GlobalName;

    w.Line($"private global::System.Collections.Generic.IAsyncEnumerable<{respT}> StreamCore_{req.Identifier}({reqT} request, global::System.Threading.CancellationToken ct)");
    w.OpenBrace();
    w.Line($"var handler = _sp.GetRequiredService<global::MDator.IStreamRequestHandler<{reqT}, {respT}>>();");
    w.Line($"global::MDator.StreamHandlerDelegate<{respT}> next = () => handler.Handle(request, ct);");

    if (openStreamBehaviors.Count > 0)
    {
      var i = 0;
      foreach (var b in ((IEnumerable<BehaviorInfo>)openStreamBehaviors).Reverse())
      {
        var bType = b.BehaviorType.GlobalNameWithoutGenerics;
        w.Line($"var __sb_{i} = _sp.GetRequiredService<{bType}<{reqT}, {respT}>>();");
        w.Line("{ var __prev = next; next = () => __sb_" + i + ".Handle(request, __prev, ct); }");
        i++;
      }
    }
    w.Line("if (!_cfg.FuseOnly)");
    w.OpenBrace();
    w.Line($"foreach (var __rb in _sp.GetServices<global::MDator.IStreamPipelineBehavior<{reqT}, {respT}>>())");
    w.OpenBrace();
    w.Line("{ var __prev2 = next; next = () => __rb.Handle(request, __prev2, ct); }");
    w.CloseBrace();
    w.CloseBrace();

    w.Line("return next();");
    w.CloseBrace();
    w.Line();
  }

  private static void EmitNotificationPublish(CodeWriter w, string _, List<HandlerInfo> handlers)
  {
    var first = handlers[0];
    var n = first.MessageType;
    var nT = n.GlobalName;

    w.Line($"private global::System.Threading.Tasks.Task PublishCore_{n.Identifier}({nT} notification, global::System.Threading.CancellationToken ct)");
    w.OpenBrace();
    w.Line($"var __all = _sp.GetServices<global::MDator.INotificationHandler<{nT}>>();");
    w.Line("var __list = new global::System.Collections.Generic.List<global::MDator.NotificationHandlerExecutor>();");
    w.Line("foreach (var __h in __all)");
    w.OpenBrace();
    w.Line($"var __hCapture = __h;");
    w.Line($"__list.Add(new global::MDator.NotificationHandlerExecutor(__hCapture, (__msg, __ct) => __hCapture.Handle(({nT})__msg, __ct)));");
    w.CloseBrace();
    w.Line("return _publisher.Publish(__list, notification, ct);");
    w.CloseBrace();
    w.Line();
  }

  // ── Cross-assembly pipeline emitters ────────────────────────────────
  //
  // These generate pipelines for request types discovered via
  // [assembly: KnownRequest(...)] on referenced assemblies. They differ
  // from same-assembly pipelines in that:
  //  • The handler is always resolved by interface type (concrete is unknown).
  //  • Pre/post processors are always resolved from DI (we don't know which exist).
  //  • No exception handler/action blocks (those are assembly-local).

  private static void EmitCrossAssemblyRequestPipeline(
      CodeWriter w, TypeRef req, TypeRef resp,
      List<BehaviorInfo> openRequestBehaviors)
  {
    var reqT = req.GlobalName;
    var respT = resp.GlobalName;

    w.Line($"private async global::System.Threading.Tasks.Task<{respT}> SendCore_{req.Identifier}({reqT} request, global::System.Threading.CancellationToken ct)");
    w.OpenBrace();
    w.Line($"var handler = _sp.GetRequiredService<global::MDator.IRequestHandler<{reqT}, {respT}>>();");
    w.Line($"var __pre = _sp.GetServices<global::MDator.IRequestPreProcessor<{reqT}>>();");
    w.Line($"var __post = _sp.GetServices<global::MDator.IRequestPostProcessor<{reqT}, {respT}>>();");
    w.Line();

    w.Line($"global::MDator.RequestHandlerDelegate<{respT}> next = async __t =>");
    w.OpenBrace();
    w.Line("var __ct = __t == default ? ct : __t;");
    w.Line("foreach (var __p in __pre) await __p.Process(request, __ct).ConfigureAwait(false);");
    w.Line("var __resp = await handler.Handle(request, __ct).ConfigureAwait(false);");
    w.Line("foreach (var __p in __post) await __p.Process(request, __resp, __ct).ConfigureAwait(false);");
    w.Line("return __resp;");
    w.CloseBraceWithSemicolon();
    w.Line();

    EmitFusedOpenBehaviors(w, openRequestBehaviors, reqT, respT);
    EmitRuntimeBehaviorEnumeration(w, reqT, respT);

    w.Line("return await next().ConfigureAwait(false);");
    w.CloseBrace();
    w.Line();
  }

  private static void EmitCrossAssemblyVoidPipeline(
      CodeWriter w, TypeRef req,
      List<BehaviorInfo> openRequestBehaviors)
  {
    var reqT = req.GlobalName;

    w.Line($"private async global::System.Threading.Tasks.Task SendVoidCore_{req.Identifier}({reqT} request, global::System.Threading.CancellationToken ct)");
    w.OpenBrace();
    w.Line($"var handler = _sp.GetRequiredService<global::MDator.IRequestHandler<{reqT}>>();");
    w.Line($"var __pre = _sp.GetServices<global::MDator.IRequestPreProcessor<{reqT}>>();");
    w.Line();

    w.Line("global::MDator.RequestHandlerDelegate<global::MDator.Unit> next = async __t =>");
    w.OpenBrace();
    w.Line("var __ct = __t == default ? ct : __t;");
    w.Line("foreach (var __p in __pre) await __p.Process(request, __ct).ConfigureAwait(false);");
    w.Line("await handler.Handle(request, __ct).ConfigureAwait(false);");
    w.Line("return global::MDator.Unit.Value;");
    w.CloseBraceWithSemicolon();
    w.Line();

    EmitFusedOpenBehaviors(w, openRequestBehaviors, reqT, "global::MDator.Unit");
    EmitRuntimeBehaviorEnumeration(w, reqT, "global::MDator.Unit");

    w.Line("await next().ConfigureAwait(false);");
    w.CloseBrace();
    w.Line();
  }

  private static void EmitCrossAssemblyStreamPipeline(
      CodeWriter w, TypeRef req, TypeRef resp,
      List<BehaviorInfo> openStreamBehaviors)
  {
    var reqT = req.GlobalName;
    var respT = resp.GlobalName;

    w.Line($"private global::System.Collections.Generic.IAsyncEnumerable<{respT}> StreamCore_{req.Identifier}({reqT} request, global::System.Threading.CancellationToken ct)");
    w.OpenBrace();
    w.Line($"var handler = _sp.GetRequiredService<global::MDator.IStreamRequestHandler<{reqT}, {respT}>>();");
    w.Line($"global::MDator.StreamHandlerDelegate<{respT}> next = () => handler.Handle(request, ct);");

    if (openStreamBehaviors.Count > 0)
    {
      var i = 0;
      foreach (var b in ((IEnumerable<BehaviorInfo>)openStreamBehaviors).Reverse())
      {
        var bType = b.BehaviorType.GlobalNameWithoutGenerics;
        w.Line($"var __sb_{i} = _sp.GetRequiredService<{bType}<{reqT}, {respT}>>();");
        w.Line("{ var __prev = next; next = () => __sb_" + i + ".Handle(request, __prev, ct); }");
        i++;
      }
    }
    w.Line("if (!_cfg.FuseOnly)");
    w.OpenBrace();
    w.Line($"foreach (var __rb in _sp.GetServices<global::MDator.IStreamPipelineBehavior<{reqT}, {respT}>>())");
    w.OpenBrace();
    w.Line("{ var __prev2 = next; next = () => __rb.Handle(request, __prev2, ct); }");
    w.CloseBrace();
    w.CloseBrace();

    w.Line("return next();");
    w.CloseBrace();
    w.Line();
  }

  private static void EmitCrossAssemblyNotificationPublish(CodeWriter w, TypeRef notification)
  {
    var nT = notification.GlobalName;

    w.Line($"private global::System.Threading.Tasks.Task PublishCore_{notification.Identifier}({nT} notification, global::System.Threading.CancellationToken ct)");
    w.OpenBrace();
    w.Line($"var __all = _sp.GetServices<global::MDator.INotificationHandler<{nT}>>();");
    w.Line("var __list = new global::System.Collections.Generic.List<global::MDator.NotificationHandlerExecutor>();");
    w.Line("foreach (var __h in __all)");
    w.OpenBrace();
    w.Line($"var __hCapture = __h;");
    w.Line($"__list.Add(new global::MDator.NotificationHandlerExecutor(__hCapture, (__msg, __ct) => __hCapture.Handle(({nT})__msg, __ct)));");
    w.CloseBrace();
    w.Line("return _publisher.Publish(__list, notification, ct);");
    w.CloseBrace();
    w.Line();
  }

  // ── Shared helpers for cross-assembly pipeline emission ─────────────

  private static void EmitFusedOpenBehaviors(
      CodeWriter w, List<BehaviorInfo> openBehaviors,
      string reqT, string respT)
  {
    if (openBehaviors.Count == 0) return;
    var i = 0;
    foreach (var b in ((IEnumerable<BehaviorInfo>)openBehaviors).Reverse())
    {
      var bType = b.BehaviorType.GlobalNameWithoutGenerics;
      w.Line($"var __b_{i} = _sp.GetRequiredService<{bType}<{reqT}, {respT}>>();");
      w.Line("{ var __prev = next; next = __t => __b_" + i + ".Handle(request, __prev, __t == default ? ct : __t); }");
      i++;
    }
    w.Line();
  }

  private static void EmitRuntimeBehaviorEnumeration(
      CodeWriter w, string reqT, string respT)
  {
    w.Line("if (!_cfg.FuseOnly)");
    w.OpenBrace();
    w.Line($"foreach (var __rb in _sp.GetServices<global::MDator.IPipelineBehavior<{reqT}, {respT}>>())");
    w.OpenBrace();
    w.Line("{ var __prev2 = next; next = __t => __rb.Handle(request, __prev2, __t == default ? ct : __t); }");
    w.CloseBrace();
    w.CloseBrace();
    w.Line();
  }
}
