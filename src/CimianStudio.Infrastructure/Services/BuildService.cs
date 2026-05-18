namespace CimianStudio.Infrastructure.Services;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CimianStudio.Core.Models.Builds;
using CimianStudio.Core.Services;
using CimianStudio.Infrastructure.Yaml;

/// <summary>
/// Drives the cimipkg toolchain on behalf of the Build tab. Project discovery
/// and YAML round-trip are pure I/O; <see cref="StreamBuildAsync"/> spawns
/// cimipkg.exe and streams stdout/stderr line by line so the UI can render a
/// live console.
/// </summary>
public sealed class BuildService : IBuildService
{
    private readonly IBuildSettingsService _buildSettings;

    public BuildService(IBuildSettingsService buildSettings)
    {
        ArgumentNullException.ThrowIfNull(buildSettings);
        _buildSettings = buildSettings;
    }

    public Task<IReadOnlyList<BuildProject>> DiscoverProjectsAsync(string projectsFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsFolder);
        return Task.Run<IReadOnlyList<BuildProject>>(() =>
        {
            if (!Directory.Exists(projectsFolder)) return [];

            var results = new List<BuildProject>();
            foreach (var dir in Directory.EnumerateDirectories(projectsFolder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var yaml = Path.Combine(dir, "build-info.yaml");
                if (!File.Exists(yaml)) continue;
                results.Add(new BuildProject
                {
                    Name = Path.GetFileName(dir),
                    DirectoryPath = dir,
                    BuildInfoPath = yaml,
                });
            }
            results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return results;
        }, cancellationToken);
    }

    public async Task<BuildInfoData?> LoadBuildInfoAsync(BuildProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!File.Exists(project.BuildInfoPath)) return null;
        var yaml = await File.ReadAllTextAsync(project.BuildInfoPath, cancellationToken).ConfigureAwait(false);
        return BuildInfoYaml.Deserialize(yaml);
    }

    public async Task SaveBuildInfoAsync(BuildProject project, BuildInfoData buildInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(buildInfo);
        var yaml = BuildInfoYaml.Serialize(buildInfo);
        // Atomic-ish write: emit to a sibling temp file then move into place so a
        // crash mid-write doesn't leave a half-truncated build-info.yaml.
        var tmp = project.BuildInfoPath + ".tmp";
        await File.WriteAllTextAsync(tmp, yaml, cancellationToken).ConfigureAwait(false);
        File.Move(tmp, project.BuildInfoPath, overwrite: true);
    }

    public IReadOnlyList<string> EnumerateScriptSlots(BuildProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        // Canonical slots cimipkg recognises (preinstall*.ps1, postinstall*.ps1,
        // uninstall.ps1 — see ScriptProcessor.cs upstream). We surface the three
        // base names always, then add any extra .ps1 files actually present.
        var slots = new List<string> { "preinstall.ps1", "postinstall.ps1", "uninstall.ps1" };
        if (Directory.Exists(project.ScriptsPath))
        {
            foreach (var file in Directory.EnumerateFiles(project.ScriptsPath, "*.ps1"))
            {
                var name = Path.GetFileName(file);
                if (!slots.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    slots.Add(name);
                }
            }
        }
        return slots;
    }

    public async Task<string> ReadScriptAsync(BuildProject project, string scriptName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptName);
        var path = Path.Combine(project.ScriptsPath, scriptName);
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    public async Task WriteScriptAsync(BuildProject project, string scriptName, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptName);
        var path = Path.Combine(project.ScriptsPath, scriptName);

        if (string.IsNullOrEmpty(content))
        {
            // Empty content removes the slot file entirely — cimipkg treats the
            // missing file as "no script for this phase". Leaving an empty .ps1
            // would still get included in the package as a no-op.
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        Directory.CreateDirectory(project.ScriptsPath);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<BuildEvent> StreamBuildAsync(
        BuildProject project,
        BuildOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);

        var tool = await GetToolInfoAsync(cancellationToken).ConfigureAwait(false);
        if (!tool.Found || string.IsNullOrEmpty(tool.Path))
        {
            yield return new BuildFailed("cimipkg.exe not found. Configure its path in Settings → Build.");
            yield break;
        }

        // Channel<T> is the cleanest way to bridge synchronous Process events
        // back into an IAsyncEnumerable consumer; it preserves ordering and
        // handles back-pressure for free.
        var channel = Channel.CreateUnbounded<BuildEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var psi = new ProcessStartInfo
        {
            FileName = tool.Path,
            WorkingDirectory = project.DirectoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in options.ToArguments(project.DirectoryPath))
        {
            psi.ArgumentList.Add(arg);
        }

        Process process = new() { StartInfo = psi };
        try
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) channel.Writer.TryWrite(new BuildLine(e.Data));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) channel.Writer.TryWrite(new BuildLine(e.Data));
            };

            bool started;
            try
            {
                started = process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                channel.Writer.TryWrite(new BuildFailed($"Could not launch cimipkg: {ex.Message}"));
                channel.Writer.Complete();
                started = false;
            }

            if (started)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Capture into a local for the closure so a later assignment to
                // `process` (none here) couldn't accidentally drift.
                var p = process;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        var productPath = DiscoverProductPath(project, options);
                        channel.Writer.TryWrite(new BuildFinished(p.ExitCode, productPath));
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) { /* already exited */ }
                        catch (System.ComponentModel.Win32Exception) { /* race */ }
                        channel.Writer.TryWrite(new BuildFailed("Build cancelled."));
                    }
                    catch (Exception ex)
                    {
                        channel.Writer.TryWrite(new BuildFailed(ex.Message));
                    }
                    finally
                    {
                        channel.Writer.Complete();
                    }
                }, cancellationToken);
            }
            else if (!channel.Reader.Completion.IsCompleted)
            {
                channel.Writer.TryWrite(new BuildFailed("cimipkg failed to start."));
                channel.Writer.Complete();
            }

            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            // Always dispose — covers the consumer breaking out of the foreach
            // mid-build (cancellation, exception in the UI handler, etc.).
            process.Dispose();
        }
    }

    public async Task<CimipkgToolInfo> GetToolInfoAsync(CancellationToken cancellationToken = default)
    {
        // Settings cache must be warm before we read BuildSettings via the
        // section accessor. The settings call is a no-op if already loaded.
        await _buildSettings.HasProjectsFolderAsync(cancellationToken).ConfigureAwait(false);

        foreach (var candidate in EnumerateCandidatePaths(_buildSettings.CimipkgPath))
        {
            if (!File.Exists(candidate)) continue;
            var version = TryReadFileVersion(candidate);
            return new CimipkgToolInfo(true, candidate, version, null);
        }

        // PATH fallback: let the OS resolve `cimipkg.exe` at process start. We
        // can't read its version this way without executing the binary, so emit
        // a stub Found result with no version.
        if (await IsOnPathAsync("cimipkg.exe", cancellationToken).ConfigureAwait(false))
        {
            return new CimipkgToolInfo(true, "cimipkg.exe", null, "on PATH");
        }

        return new CimipkgToolInfo(false, null, null, "Not found. Set the executable path in Settings.");
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return overridePath;
        }

        // Standard install location for the Cimian MSI on Windows. Admins
        // running CimianStudio are expected to have Cimian installed locally
        // — the app is a GUI front-end for the Cimian CLIs, not a builder.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, "Cimian", "cimipkg.exe");
        }
    }

    private static string? TryReadFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrEmpty(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static Task<bool> IsOnPathAsync(string exeName, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv)) return false;
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(Path.Combine(dir, exeName))) return true;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
            return false;
        }, cancellationToken);
    }

    private static string? DiscoverProductPath(BuildProject project, BuildOptions options)
    {
        // Match cimipkg's output convention: build artefacts land in {project}/build.
        // Prefer the format the user asked for; fall back to whatever exists.
        var buildDir = project.BuildOutputPath;
        if (!Directory.Exists(buildDir)) return null;

        var preferredExt = options.BuildIntunewin ? ".intunewin"
            : options.BuildNupkg ? ".nupkg"
            : ".msi";

        var preferred = Directory.EnumerateFiles(buildDir, "*" + preferredExt)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (preferred is not null) return preferred;

        // Fall back to any package-like artefact (whichever was produced last).
        return Directory.EnumerateFiles(buildDir)
            .Where(f =>
            {
                var ext = Path.GetExtension(f);
                return string.Equals(ext, ".msi", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".nupkg", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".intunewin", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
