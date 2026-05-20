namespace CimianStudio.Core.Models.Builds;

/// <summary>
/// Stream events from <see cref="Services.IBuildService.StreamBuildAsync"/>. The
/// build runs until a single <see cref="BuildFinished"/> or <see cref="BuildFailed"/>
/// is emitted; <see cref="BuildLine"/> can appear any number of times before that.
/// </summary>
public abstract record BuildEvent;

public sealed record BuildLine(string Text) : BuildEvent;

/// <summary>Build process exited normally. <paramref name="ProductPath"/> is the
/// discovered output artifact (.msi / .nupkg) or null when one could not be found.</summary>
public sealed record BuildFinished(int ExitCode, string? ProductPath) : BuildEvent;

/// <summary>Process failed to start (cimipkg missing, etc.).</summary>
public sealed record BuildFailed(string Reason) : BuildEvent;
