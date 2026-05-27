namespace CimianStudio.Infrastructure.Services;

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using CimianStudio.Core.Services;

public sealed class PackageInspectorService : IPackageInspectorService
{
    private const string ExecutableName = "pkginspector.exe";
    private const string ReleasesApiUrl = "https://api.github.com/repos/windowsadmins/pkg-inspector/releases/latest";
    private const string UserAgent = "CimianStudio";

    private static readonly string[] InspectableExtensions = [".pkg", ".nupkg", ".msi"];

    public Uri DownloadPageUrl { get; } = new Uri("https://github.com/windowsadmins/pkg-inspector/releases/latest");

    public bool CanInspect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return InspectableExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public string? FindExecutable()
    {
        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate)) return candidate;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var probe = Path.Combine(dir.Trim(), ExecutableName);
                    if (File.Exists(probe)) return probe;
                }
                catch (ArgumentException) { }
            }
        }

        return null;
    }

    public async Task<bool> OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var exe = FindExecutable();
        if (exe is null) return false;
        if (!File.Exists(filePath)) return false;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        psi.ArgumentList.Add(filePath);
        try
        {
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<PackageInspectorInstallResult> InstallLatestAsync(IProgress<PackageInspectorInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report(new("Querying GitHub for the latest release."));
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var release = await http.GetFromJsonAsync<GitHubRelease>(ReleasesApiUrl, cancellationToken).ConfigureAwait(false);
            if (release is null || release.Assets is null || release.Assets.Count == 0)
            {
                return new(false, null, "Unable to read the latest release from GitHub.");
            }

            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var asset = release.Assets.FirstOrDefault(a => a.Name?.Contains(arch, StringComparison.OrdinalIgnoreCase) == true && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null || string.IsNullOrEmpty(asset.BrowserDownloadUrl))
            {
                return new(false, null, $"Latest release has no {arch} zip asset.");
            }

            var tempZip = Path.Combine(Path.GetTempPath(), $"pkginspector-{Guid.NewGuid():N}.zip");
            progress?.Report(new($"Downloading {asset.Name} ({FormatBytes(asset.Size)})."));

            using (var response = await http.GetAsync(new Uri(asset.BrowserDownloadUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? asset.Size;
                var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (src.ConfigureAwait(false))
                {
                    var dst = File.Create(tempZip);
                    await using (dst.ConfigureAwait(false))
                    {
                        var buffer = new byte[81920];
                        long received = 0;
                        int read;
                        while ((read = await src.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            received += read;
                            if (total > 0)
                            {
                                progress?.Report(new($"Downloading {asset.Name}", (double)received / total));
                            }
                        }
                    }
                }
            }

            progress?.Report(new("Installing to %LOCALAPPDATA%\\Programs\\PackageInspector."));
            var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PackageInspector");

            try
            {
                if (Directory.Exists(installDir))
                {
                    Directory.Delete(installDir, recursive: true);
                }
            }
            catch (IOException ex)
            {
                return new(false, null, $"Couldn't clear the existing install at {installDir}: {ex.Message}. Close pkginspector.exe and try again.");
            }

            Directory.CreateDirectory(installDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, installDir, overwriteFiles: true), cancellationToken).ConfigureAwait(false);

            try { File.Delete(tempZip); } catch { }

            var exe = Directory.EnumerateFiles(installDir, ExecutableName, SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null)
            {
                return new(false, null, $"Extracted archive does not contain {ExecutableName}.");
            }

            CreateStartMenuShortcut(exe);
            progress?.Report(new("Installed."));
            return new(true, exe, null);
        }
        catch (OperationCanceledException)
        {
            return new(false, null, "Install cancelled.");
        }
        catch (Exception ex)
        {
            return new(false, null, ex.Message);
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(local, "Programs", "PackageInspector", ExecutableName);
        yield return Path.Combine(programs, "PackageInspector", ExecutableName);
        if (!string.IsNullOrEmpty(programsX86))
            yield return Path.Combine(programsX86, "PackageInspector", ExecutableName);
        yield return Path.Combine(programs, "pkg-inspector", ExecutableName);
    }

    private static void CreateStartMenuShortcut(string exePath)
    {
        try
        {
            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            var shortcutPath = Path.Combine(startMenu, "Package Inspector.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return;
            try
            {
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
                shortcut.IconLocation = $"{exePath},0";
                shortcut.Description = "Inspect Windows installer packages";
                shortcut.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            // Shortcut is best-effort.
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:0.#} {units[i]}";
    }

    private sealed class GitHubRelease
    {
        [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("size")]
        public long Size { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
