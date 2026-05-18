namespace CimianStudio.Core.Models.Imports;

/// <summary>
/// Stream events from <see cref="Services.ICimiimportService.RunAsync"/>.
/// Terminates with a single <see cref="ImportFinished"/> or <see cref="ImportFailed"/>;
/// <see cref="ImportLine"/> can appear any number of times before that.
/// </summary>
public abstract record ImportEvent;

public sealed record ImportLine(string Text) : ImportEvent;

/// <summary>cimiimport exited normally. Paths are derived by snapshotting
/// <c>pkgsinfo/</c> and <c>pkgs/</c> before and after the run.</summary>
public sealed record ImportFinished(int ExitCode, string? PkginfoPath, string? InstallerPath) : ImportEvent;

/// <summary>Process failed to start (cimiimport missing, etc.).</summary>
public sealed record ImportFailed(string Reason) : ImportEvent;
