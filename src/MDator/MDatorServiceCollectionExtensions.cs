using Microsoft.Extensions.DependencyInjection;

namespace MDator;

/// <summary>
/// Entry point for registering MDator in an <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// <para>
/// The actual handler / behavior registrations are emitted by the source generator
/// into a <c>MDatorGeneratedRegistration</c> class in each consuming assembly that
/// declares MDator handlers. Each such class installs a module initializer that
/// appends its registration callback to <see cref="MDatorGeneratedHook.Registrations"/>.
/// </para>
/// <para>
/// When <see cref="AddMDator"/> is called, every accumulated callback runs in order,
/// so handlers spread across multiple projects are all picked up at the composition
/// root without the user needing to enumerate them.
/// </para>
/// </remarks>
public static class MDatorServiceCollectionExtensions
{
  /// <summary>
  /// Registers MDator services and behaviors within the specified IServiceCollection.
  /// </summary>
  /// <param name="services">
  /// The <see cref="IServiceCollection"/> to add the MDator services to.
  /// </param>
  /// <param name="configure">
  /// An optional configuration delegate to customize the behavior of MDator.
  /// If not specified, default configuration is used.
  /// </param>
  /// <returns>
  /// The modified <see cref="IServiceCollection"/> with MDator services registered.
  /// </returns>
  /// <exception cref="ArgumentNullException">
  /// Thrown if the <paramref name="services"/> parameter is null.
  /// </exception>
  /// <exception cref="InvalidOperationException">
  /// Thrown if no MDator handler registrations are found in the current application context.
  /// </exception>
  public static IServiceCollection AddMDator(this IServiceCollection services,
      Action<MDatorConfiguration>? configure = null)
  {
    if (services is null) throw new ArgumentNullException(nameof(services));

    var cfg = new MDatorConfiguration();
    configure?.Invoke(cfg);

    if (MDatorGeneratedHook.Registrations.Count == 0)
    {
      throw new InvalidOperationException(
          "MDator: no generated registration callbacks were found. Make sure at least one " +
          "project that declares MDator handlers references the MDator package so the source " +
          "generator runs, and that it actually contains at least one handler.");
    }

    // Each callback must run at most once per service collection: composition
    // roots that call AddMDator once per feature module would otherwise replay
    // every callback per call, registering each handler N times — and
    // INotificationHandlers then fire N times per publish. A marker descriptor
    // on the collection records how many callbacks have already been applied.
    var marker = (AppliedRegistrationsMarker?)services
      .FirstOrDefault(d => d.ServiceType == typeof(AppliedRegistrationsMarker))
      ?.ImplementationInstance;
    if (marker is null)
    {
      marker = new AppliedRegistrationsMarker();
      services.AddSingleton(marker);
    }

    // Index loop, not foreach: invoking a registration callback can
    // trigger lazy load of a referenced handler-bearing assembly, whose
    // module initializer appends to MDatorGeneratedHook.Registrations. We
    // want those late arrivals to register too, so we re-read Count each
    // iteration rather than snapshotting (and we'd crash with "Collection
    // was modified" under foreach).
    int applied;
    for (applied = marker.AppliedCount; applied < MDatorGeneratedHook.Registrations.Count; applied++)
    {
      MDatorGeneratedHook.Registrations[applied](services, cfg);
    }
    marker.AppliedCount = applied;

    foreach (var (serviceType, implementationType, lifetime) in cfg.AdditionalBehaviors)
    {
      services.Add(new ServiceDescriptor(serviceType, implementationType, lifetime));
    }

    services.AddSingleton(cfg);
    if (cfg.NotificationPublisherType is { } publisherType)
    {
      if (!typeof(INotificationPublisher).IsAssignableFrom(publisherType))
      {
        throw new InvalidOperationException(
            $"{publisherType.Name} must implement INotificationPublisher");
      }
      services.Add(new ServiceDescriptor(typeof(INotificationPublisher), publisherType, ServiceLifetime.Singleton));
    }
    else
    {
      services.AddSingleton(cfg.NotificationPublisher);
    }

    return services;
  }

  /// <summary>
  /// Per-<see cref="IServiceCollection"/> record of how many callbacks from
  /// <see cref="MDatorGeneratedHook.Registrations"/> have been applied, so
  /// repeated <see cref="AddMDator"/> calls don't replay them.
  /// </summary>
  private sealed class AppliedRegistrationsMarker
  {
    public int AppliedCount { get; set; }
  }
}

/// <summary>
/// Bridge between the runtime MDator library and generator-emitted registration code
/// in consuming assemblies. Each consuming assembly that contains handlers emits a
/// module initializer that <see cref="List{T}.Add"/>s to <see cref="Registrations"/>.
/// </summary>
public static class MDatorGeneratedHook
{
  /// <summary>
  /// Registration callbacks contributed by generated code in consuming assemblies.
  /// Populated by module initializers at assembly load time; drained (but not
  /// cleared) when <see cref="MDatorServiceCollectionExtensions.AddMDator"/> runs.
  /// </summary>
  public static List<Action<IServiceCollection, MDatorConfiguration>> Registrations { get; } = new();
}
