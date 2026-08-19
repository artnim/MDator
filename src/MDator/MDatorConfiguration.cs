using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace MDator;

/// <summary>
/// Runtime configuration passed to <c>AddMDator(cfg =&gt; ...)</c>. The source generator
/// is authoritative for handler and open-behavior discovery; this object exists for
/// the parts that genuinely have to be chosen at runtime: lifetime defaults,
/// notification publisher strategy, and dynamically-added closed behaviors.
/// </summary>
public sealed class MDatorConfiguration
{
  /// <summary>
  /// Default lifetime for handlers, pre/post processors and closed behaviors
  /// registered by the generator. <see cref="ServiceLifetime.Transient"/> matches
  /// MediatR's default.
  /// </summary>
  public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;

  /// <summary>
  /// When <c>true</c>, generated pipelines only execute behaviors declared via
  /// <see cref="OpenBehaviorAttribute"/> (and closed behaviors referenced by type
  /// in the consuming assembly). The per-request
  /// <c>sp.GetServices&lt;IPipelineBehavior&lt;,&gt;&gt;()</c> enumeration fallback
  /// is skipped, which eliminates its allocation and gives fully compile-time
  /// fused pipelines. Runtime-added behaviors will be ignored.
  /// </summary>
  public bool FuseOnly { get; set; }

  /// <summary>
  /// Publisher strategy used by <c>IPublisher.Publish</c>. Defaults to
  /// <see cref="ForeachAwaitPublisher"/> which matches MediatR's default.
  /// Ignored when <see cref="NotificationPublisherType"/> is set.
  /// </summary>
  public INotificationPublisher NotificationPublisher { get; set; } = new ForeachAwaitPublisher();

  /// <summary>
  /// Publisher strategy registered by type and resolved from the container,
  /// so it can take constructor dependencies. When set, this wins over
  /// <see cref="NotificationPublisher"/>. Mirrors MediatR's property of the
  /// same name.
  /// </summary>
  public Type? NotificationPublisherType { get; set; }

  /// <summary>
  /// Additional closed pipeline behaviors to register at runtime. The generator
  /// already picks up closed behaviors implementing
  /// <see cref="IPipelineBehavior{TRequest, TResponse}"/> directly; use this only
  /// for behaviors that live in an assembly the generator didn't scan, or that you
  /// want to enable conditionally.
  /// </summary>
  public List<(Type ServiceType, Type ImplementationType, ServiceLifetime Lifetime)> AdditionalBehaviors { get; } = new();

  /// <summary>
  /// MediatR source-compatibility shim. Has no effect — MDator's source generator
  /// scans the consuming compilation directly, so handler discovery is automatic.
  /// The analyzer <c>MDATOR0001</c> flags calls to this method.
  /// </summary>
  public MDatorConfiguration RegisterServicesFromAssemblyContaining<T>() => this;

  /// <summary>
  /// MediatR source-compatibility shim. Has no effect — MDator's source generator
  /// scans the consuming compilation directly, so handler discovery is automatic.
  /// The analyzer <c>MDATOR0001</c> flags calls to this method.
  /// </summary>
  public MDatorConfiguration RegisterServicesFromAssembly(System.Reflection.Assembly assembly) => this;

  /// <summary>
  /// MediatR source-compatibility shim. Has no effect — MDator's source generator
  /// scans the consuming compilation directly, so handler discovery is automatic.
  /// The analyzer <c>MDATOR0001</c> flags calls to this method.
  /// </summary>
  public MDatorConfiguration RegisterServicesFromAssemblies(params System.Reflection.Assembly[] assemblies) => this;

  /// <summary>
  /// Source-compat marker for MediatR v12 migration.
  /// Open generic handlers are discovered automatically by the source generator.
  /// </summary>
  public bool RegisterGenericHandlers { get; set; }

  /// <summary>
  /// Open behavior types registered by the source generator's <c>[ModuleInitializer]</c>.
  /// Used by <see cref="RuntimeDispatch"/> to chain open behaviors in the DI fallback
  /// path (cross-assembly handler dispatch).
  /// </summary>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public List<(Type Type, int Order)> OpenBehaviorTypes { get; } = new();

  /// <summary>
  /// Called from generated registration code to record an open behavior type
  /// so the runtime fallback path can resolve it.
  /// </summary>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public void RegisterOpenBehavior(Type type, int order)
  {
    if (OpenBehaviorTypes.All(x => x.Type != type))
      OpenBehaviorTypes.Add((type, order));
  }

  /// <summary>
  /// Registers a closed behavior at runtime.
  /// </summary>
  public MDatorConfiguration AddBehavior<TImplementation>(ServiceLifetime lifetime = ServiceLifetime.Transient)
      where TImplementation : class
  {
    AdditionalBehaviors.Add((typeof(TImplementation), typeof(TImplementation), lifetime));
    return this;
  }

  /// <summary>
  /// Registers a closed behavior at runtime against a specific service type.
  /// </summary>
  public MDatorConfiguration AddBehavior<TServiceType, TImplementationType>(ServiceLifetime lifetime = ServiceLifetime.Transient)
      where TImplementationType : class, TServiceType
  {
    AdditionalBehaviors.Add((typeof(TServiceType), typeof(TImplementationType), lifetime));
    return this;
  }

  /// <summary>
  /// Registers a closed behavior at runtime against a specific service type.
  /// </summary>
  public MDatorConfiguration AddBehavior(Type serviceType, Type implementationType, ServiceLifetime lifetime = ServiceLifetime.Transient)
  {
    AdditionalBehaviors.Add((serviceType, implementationType, lifetime));
    return this;
  }

  /// <summary>
  /// Registers an open generic <see cref="IPipelineBehavior{TRequest, TResponse}"/>
  /// implementation, closed per request type by the container. Mirrors MediatR's
  /// <c>AddOpenBehavior</c>. Runs on the runtime enumeration path, so it is
  /// skipped when <see cref="FuseOnly"/> is enabled — use
  /// <see cref="OpenBehaviorAttribute"/> for compile-time fusion instead.
  /// </summary>
  public MDatorConfiguration AddOpenBehavior(Type openBehaviorType, ServiceLifetime lifetime = ServiceLifetime.Transient)
  {
    RequireOpenImplementationOf(openBehaviorType, typeof(IPipelineBehavior<,>));
    AdditionalBehaviors.Add((typeof(IPipelineBehavior<,>), openBehaviorType, lifetime));
    return this;
  }

  /// <summary>
  /// Registers multiple open generic behaviors. Mirrors MediatR's <c>AddOpenBehaviors</c>.
  /// </summary>
  public MDatorConfiguration AddOpenBehaviors(IEnumerable<Type> openBehaviorTypes, ServiceLifetime lifetime = ServiceLifetime.Transient)
  {
    foreach (var type in openBehaviorTypes) AddOpenBehavior(type, lifetime);
    return this;
  }

  /// <summary>
  /// Registers a closed stream behavior at runtime.
  /// </summary>
  public MDatorConfiguration AddStreamBehavior<TImplementationType>(ServiceLifetime lifetime = ServiceLifetime.Transient)
      where TImplementationType : class
  {
    return AddStreamBehavior(typeof(TImplementationType), lifetime);
  }

  /// <summary>
  /// Registers a closed stream behavior at runtime against a specific service type.
  /// </summary>
  public MDatorConfiguration AddStreamBehavior<TServiceType, TImplementationType>(ServiceLifetime lifetime = ServiceLifetime.Transient)
      where TImplementationType : class, TServiceType
  {
    AdditionalBehaviors.Add((typeof(TServiceType), typeof(TImplementationType), lifetime));
    return this;
  }

  /// <summary>
  /// Registers a closed stream behavior at runtime under every
  /// <see cref="IStreamPipelineBehavior{TRequest, TResponse}"/> interface it implements.
  /// </summary>
  public MDatorConfiguration AddStreamBehavior(Type implementationType, ServiceLifetime lifetime = ServiceLifetime.Transient)
  {
    var serviceTypes = implementationType.GetInterfaces()
        .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamPipelineBehavior<,>))
        .ToList();
    if (serviceTypes.Count == 0)
    {
      throw new InvalidOperationException(
          $"{implementationType.Name} must implement IStreamPipelineBehavior<TRequest, TResponse>");
    }
    foreach (var serviceType in serviceTypes)
      AdditionalBehaviors.Add((serviceType, implementationType, lifetime));
    return this;
  }

  /// <summary>
  /// Registers a closed stream behavior at runtime against a specific service type.
  /// </summary>
  public MDatorConfiguration AddStreamBehavior(Type serviceType, Type implementationType, ServiceLifetime lifetime = ServiceLifetime.Transient)
  {
    AdditionalBehaviors.Add((serviceType, implementationType, lifetime));
    return this;
  }

  /// <summary>
  /// Registers an open generic <see cref="IStreamPipelineBehavior{TRequest, TResponse}"/>
  /// implementation, closed per request type by the container. Runs on the runtime
  /// enumeration path, so it is skipped when <see cref="FuseOnly"/> is enabled.
  /// </summary>
  public MDatorConfiguration AddOpenStreamBehavior(Type openBehaviorType, ServiceLifetime lifetime = ServiceLifetime.Transient)
  {
    RequireOpenImplementationOf(openBehaviorType, typeof(IStreamPipelineBehavior<,>));
    AdditionalBehaviors.Add((typeof(IStreamPipelineBehavior<,>), openBehaviorType, lifetime));
    return this;
  }

  private static void RequireOpenImplementationOf(Type openBehaviorType, Type openInterface)
  {
    var implementsInterface = openBehaviorType.IsGenericTypeDefinition && openBehaviorType
        .GetInterfaces()
        .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == openInterface);
    if (!implementsInterface)
    {
      throw new InvalidOperationException(
          $"{openBehaviorType.Name} must be an open generic implementing {openInterface.Name}");
    }
  }
}
