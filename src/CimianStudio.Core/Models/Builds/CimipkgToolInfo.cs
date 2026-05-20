namespace CimianStudio.Core.Models.Builds;

/// <summary>Result of <see cref="Services.IBuildService.GetToolInfoAsync"/>.</summary>
public sealed record CimipkgToolInfo(bool Found, string? Path, string? Version, string? Detail);
