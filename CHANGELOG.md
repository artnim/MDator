# Changelog

All notable changes to MDator will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases are cut by tagging `vX.Y.Z` on `main`; the publish workflow then packs
and pushes to NuGet. GitHub auto-generated release notes cover the full commit
list — this file curates the user-visible changes.

## [Unreleased]

## [0.6.2] - 2026-08-10

### Fixed

- The NuGet package again ships analyzer variants actually compiled against
  Roslyn 4.8, 4.12, and 5.0. Since 0.5.0 all three variants were silently
  built against one and the same Roslyn version, so consumers on older
  compilers could hit CS9057 once that version moved ahead of their SDK.

### Changed

- Bumped shipped runtime dependencies `Microsoft.Bcl.AsyncInterfaces` and
  `Microsoft.Extensions.DependencyInjection.Abstractions` to 10.0.10.

## [0.6.1] - 2026-06-12

### Fixed

- `AddMDator` no longer replays every generated registration callback on each
  call. Composition roots that call `AddMDator` once per feature module ended
  up registering every handler once per call — notification handlers then
  fired that many times per `Publish` (seen in production as background jobs
  executing 26 times). Callbacks are now applied at most once per
  `IServiceCollection`; calling `AddMDator` on a fresh collection still
  registers everything.

## [0.6.0] - 2026-05-18

### Changed

- The generated `MDatorGeneratedRegistration` class is now decorated with
  `[ExcludeFromCodeCoverage]` so it is no longer counted in consumer coverage
  reports. Thanks to @MPapst (#48).

## [0.5.0] - 2026-05-06

### Fixed

- `AddMDator` no longer throws `InvalidOperationException: Collection was
  modified` when a registration callback indirectly triggers loading of another
  handler-bearing assembly mid-iteration. The iteration over
  `MDatorGeneratedHook.Registrations` now uses an index loop so module
  initializers that append during the call are picked up too.

### Added

- `RuntimeDispatch.PublishFallback` dispatches unknown notification types when
  no compile-time switch arm matches.
- `MDATOR0001` analyzer flags no-op MediatR-compat shim methods.

## [0.4.0]

- Cache compiled delegates in `RuntimeDispatch` fallback paths.
- Pin vulnerable transitive dependencies to fixed versions.
- Add missing XML comments and remove the `CS1591` suppression.

For prior releases see the [GitHub Releases](https://github.com/ArtnimIO/MDator/releases).

[Unreleased]: https://github.com/ArtnimIO/MDator/compare/v0.6.2...HEAD
[0.6.2]: https://github.com/ArtnimIO/MDator/compare/v0.6.1...v0.6.2
[0.6.1]: https://github.com/ArtnimIO/MDator/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/ArtnimIO/MDator/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/ArtnimIO/MDator/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/ArtnimIO/MDator/compare/v0.3.0...v0.4.0
