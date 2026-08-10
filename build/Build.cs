using System.IO.Compression;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Solutions;
using Fallout.Common.Tools.DotNet;
using Serilog;

class Build : FalloutBuild
{
  public static int Main() => Execute<Build>(x => x.Pack);

  [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
  readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

  [Parameter("Package version (e.g. 1.0.0). Defaults to '0.0.0-local' for local builds")]
  readonly string Version = "0.0.0-local";

  [Parameter("NuGet API key for publishing packages")]
  [Secret]
  readonly string? NuGetApiKey;

  [Solution]
  readonly Solution Solution = null!;

  AbsolutePath OutputDirectory => RootDirectory / "output";

  /// <summary>
  /// Roslyn versions to multi-target the source generator against.
  /// The .NET SDK automatically picks the highest compatible version from
  /// <c>analyzers/roslyn&lt;ver&gt;/dotnet/cs/</c> inside the nupkg.
  /// </summary>
  static readonly string[] RoslynVersions = ["4.8", "4.12", "5.0"];

  AbsolutePath SourceGeneratorProject =>
      RootDirectory / "src" / "MDator.SourceGenerator" / "MDator.SourceGenerator.csproj";

  AbsolutePath MDatorProject =>
      RootDirectory / "src" / "MDator" / "MDator.csproj";

  AbsolutePath AbstractionsProject =>
      RootDirectory / "src" / "MDator.Abstractions" / "MDator.Abstractions.csproj";

  Target Clean => _ => _
      .Before(Restore)
      .Executes(() =>
      {
        OutputDirectory.CreateOrCleanDirectory();
      });

  Target Restore => _ => _
      .Executes(() =>
      {
        DotNetTasks.DotNetRestore(s => s
              .SetProjectFile(Solution));
      });

  Target Compile => _ => _
      .DependsOn(Restore)
      .Executes(() =>
      {
        DotNetTasks.DotNetBuild(s => s
              .SetProjectFile(Solution)
              .SetConfiguration(Configuration)
              .SetVersion(Version)
              .SetNoRestore(true));
      });

  Target Test => _ => _
      .DependsOn(Compile)
      .Executes(() =>
      {
        DotNetTasks.DotNetTest(s => s
              .SetProjectFile(Solution)
              .SetConfiguration(Configuration)
              .SetNoBuild(true));
      });

  Target Pack => _ => _
      .DependsOn(Test)
      .Executes(() =>
      {
        // MDator.Abstractions — no analyzer, straightforward pack.
        DotNetTasks.DotNetPack(s => s
              .SetProject(AbstractionsProject)
              .SetConfiguration(Configuration)
              .SetNoBuild(true)
              .SetVersion(Version)
              .SetOutputDirectory(OutputDirectory));

        // MDator — multi-Roslyn pack: build the source generator once per
        // Roslyn version and produce a per-version nupkg, then merge them.
        AbsolutePath stagingDir = OutputDirectory / "roslyn-staging";
        stagingDir.CreateOrCleanDirectory();

        foreach (var roslynVersion in RoslynVersions)
        {
          Log.Information("Building source generator for Roslyn {Version}", roslynVersion);

          // Restore + build the generator for this Roslyn version (the
          // Microsoft.CodeAnalysis.CSharp PackageReference changes).
          DotNetTasks.DotNetBuild(s => s
                .SetProjectFile(SourceGeneratorProject)
                .SetConfiguration(Configuration)
                .SetVersion(Version)
                .SetProperty("ROSLYN_VERSION", roslynVersion));

          // Pack MDator.csproj which bundles the freshly-built generator DLL.
          // --no-build is safe: the MDator runtime assemblies (net9.0/net10.0)
          // were already compiled during the Compile step and don't change.
          AbsolutePath versionOutputDir = stagingDir / $"roslyn-{roslynVersion}";
          DotNetTasks.DotNetPack(s => s
                .SetProject(MDatorProject)
                .SetConfiguration(Configuration)
                .SetNoBuild(true)
                .SetVersion(Version)
                .SetProperty("ROSLYN_VERSION", roslynVersion)
                .SetOutputDirectory(versionOutputDir));
        }

        MergeNupkgs(stagingDir, OutputDirectory);
        stagingDir.DeleteDirectory();
      });

  Target SampleCompile => _ => _
      .DependsOn(Pack)
      .Executes(() =>
      {
        var samplesSolution = RootDirectory / "samples" / "Samples.slnx";

        DotNetTasks.DotNetRestore(s => s
              .SetProjectFile(samplesSolution)
              .AddSources(OutputDirectory));

        DotNetTasks.DotNetBuild(s => s
              .SetProjectFile(samplesSolution)
              .SetConfiguration(Configuration)
              .SetNoRestore(true));
      });

  Target Publish => _ => _
      .DependsOn(Pack)
      .Requires(() => NuGetApiKey)
      .Executes(() =>
      {
        var packages = OutputDirectory.GlobFiles("*.nupkg");
        Log.Information("Publishing {Count} package(s)", packages.Count);

        foreach (var package in packages)
        {
          DotNetTasks.DotNetNuGetPush(s => s
                .SetTargetPath(package)
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetApiKey(NuGetApiKey)
                .SetSkipDuplicate(true));
        }
      });

  /// <summary>
  /// Merges per-Roslyn-version nupkg files into a single nupkg.
  /// Each per-version nupkg is identical except for the
  /// <c>analyzers/roslyn&lt;ver&gt;/dotnet/cs/</c> entry.
  /// Common entries (lib/, README, .nuspec, etc.) are deduplicated.
  /// </summary>
  static void MergeNupkgs(AbsolutePath stagingDir, AbsolutePath outputDir)
  {
    var allNupkgs = stagingDir.GlobFiles("**/*.nupkg")
        .OrderBy(p => p.ToString())
        .ToList();

    if (allNupkgs.Count == 0)
      throw new InvalidOperationException("No nupkg files found in staging directory");

    // Use the first nupkg as the base, then add unique entries from the rest.
    var baseNupkg = allNupkgs[0];
    var mergedPath = outputDir / baseNupkg.Name;
    File.Copy(baseNupkg, mergedPath, overwrite: true);

    Log.Information("Merging {Count} nupkg(s) into {Target}", allNupkgs.Count, mergedPath);

    using var mergedZip = ZipFile.Open(mergedPath, ZipArchiveMode.Update);
    var existingEntries = mergedZip.Entries
        .Select(e => e.FullName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    for (var i = 1; i < allNupkgs.Count; i++)
    {
      using var sourceZip = ZipFile.OpenRead(allNupkgs[i]);
      foreach (var entry in sourceZip.Entries)
      {
        if (existingEntries.Contains(entry.FullName))
          continue;

        var newEntry = mergedZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
        using var sourceStream = entry.Open();
        using var targetStream = newEntry.Open();
        sourceStream.CopyTo(targetStream);

        existingEntries.Add(entry.FullName);
      }
    }

    Log.Information("Merged nupkg: {Path}", mergedPath);
  }
}
