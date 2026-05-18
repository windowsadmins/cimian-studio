namespace CimianStudio.Infrastructure.Services;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CimianStudio.Core.Models.Imports;
using CimianStudio.Core.Models.Repository;
using CimianStudio.Core.Services;

/// <summary>
/// Runs <c>cimiimport.exe</c> for the Import tab. Streams stdout/stderr line
/// by line through a <see cref="Channel{T}"/>, and emits exactly one terminal
/// <see cref="ImportFinished"/> or <see cref="ImportFailed"/> event when the
/// process exits. Pkginfo + installer paths are recovered by diffing the
/// repository before / after the run — cimiimport places files based on its
/// own (template / detection) logic, which we don't try to predict.
/// </summary>
public sealed class CimiimportService : ICimiimportService
{
    private readonly IImportSettingsService _importSettings;

    public CimiimportService(IImportSettingsService importSettings)
    {
        ArgumentNullException.ThrowIfNull(importSettings);
        _importSettings = importSettings;
    }

    public async IAsyncEnumerable<ImportEvent> RunAsync(
        CimianRepository repository,
        CimiimportOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);

        var tool = await GetToolInfoAsync(cancellationToken).ConfigureAwait(false);
        if (!tool.Found || string.IsNullOrEmpty(tool.Path))
        {
            yield return new ImportFailed("cimiimport.exe not found. Install Cimian (https://github.com/windowsadmins/cimian/releases) or configure its path in Settings → Import.");
            yield break;
        }

        // Default the repo path to the open repository when the caller didn't
        // explicitly set one — keeps the wizard from having to think about
        // cimiimport's config-file fallback.
        if (string.IsNullOrWhiteSpace(options.RepoPath))
        {
            options.RepoPath = repository.RootPath;
        }

        // Snapshot pkgsinfo / pkgs before the run so we can diff and recover
        // the produced paths after success. Both filesystems are small (a
        // single repo, hundreds of files), so a full enumeration is cheap.
        var pkgsInfoBefore = SnapshotFiles(repository.PkgsInfoPath);
        var pkgsBefore = SnapshotFiles(repository.PkgsPath);

        var channel = Channel.CreateUnbounded<ImportEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var psi = new ProcessStartInfo
        {
            FileName = tool.Path,
            WorkingDirectory = repository.RootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in options.ToArguments())
        {
            psi.ArgumentList.Add(arg);
        }

        Process process = new() { StartInfo = psi };
        try
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) channel.Writer.TryWrite(new ImportLine(e.Data));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) channel.Writer.TryWrite(new ImportLine(e.Data));
            };

            bool started;
            try
            {
                started = process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                channel.Writer.TryWrite(new ImportFailed($"Could not launch cimiimport: {ex.Message}"));
                channel.Writer.Complete();
                started = false;
            }

            if (started)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var p = process;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        var (pkginfo, installer) = ResolveProducedPaths(repository, pkgsInfoBefore, pkgsBefore);
                        channel.Writer.TryWrite(new ImportFinished(p.ExitCode, pkginfo, installer));
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) { /* already exited */ }
                        catch (System.ComponentModel.Win32Exception) { /* race */ }
                        channel.Writer.TryWrite(new ImportFailed("Import cancelled."));
                    }
                    catch (Exception ex)
                    {
                        channel.Writer.TryWrite(new ImportFailed(ex.Message));
                    }
                    finally
                    {
                        channel.Writer.Complete();
                    }
                }, cancellationToken);
            }
            else if (!channel.Reader.Completion.IsCompleted)
            {
                channel.Writer.TryWrite(new ImportFailed("cimiimport failed to start."));
                channel.Writer.Complete();
            }

            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task<CimiimportToolInfo> GetToolInfoAsync(CancellationToken cancellationToken = default)
    {
        await _importSettings.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        foreach (var candidate in EnumerateCandidatePaths(_importSettings.CimiimportPath))
        {
            if (!File.Exists(candidate)) continue;
            return new CimiimportToolInfo(true, candidate, TryReadFileVersion(candidate), null);
        }

        if (await IsOnPathAsync("cimiimport.exe", cancellationToken).ConfigureAwait(false))
        {
            return new CimiimportToolInfo(true, "cimiimport.exe", null, "on PATH");
        }

        return new CimiimportToolInfo(false, null, null, "cimiimport.exe not installed. Install Cimian from github.com/windowsadmins/cimian/releases.");
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return overridePath;
        }

        // Standard install location for the Cimian MSI on Windows. Admins
        // running CimianStudio are expected to have Cimian installed locally
        // (the app is GUI-only — it never builds or imports without the CLI).
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, "Cimian", "cimiimport.exe");
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

    private static HashSet<string> SnapshotFiles(string root)
    {
        if (!Directory.Exists(root)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
            StringComparer.OrdinalIgnoreCase);
    }

    private static (string? PkginfoPath, string? InstallerPath) ResolveProducedPaths(
        CimianRepository repo,
        HashSet<string> pkgsInfoBefore,
        HashSet<string> pkgsBefore)
    {
        var pkginfo = FindNewest(repo.PkgsInfoPath, pkgsInfoBefore, ".yaml");
        var installer = FindNewest(repo.PkgsPath, pkgsBefore, null);
        return (pkginfo, installer);
    }

    private static string? FindNewest(string root, HashSet<string> snapshot, string? requiredExtension)
    {
        if (!Directory.Exists(root)) return null;
        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (snapshot.Contains(file)) continue;
            if (requiredExtension is not null
                && !string.Equals(Path.GetExtension(file), requiredExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var t = File.GetLastWriteTimeUtc(file);
            if (t > bestTime)
            {
                bestTime = t;
                best = file;
            }
        }
        return best;
    }
}
