namespace CimianStudio.Core.Models.Imports;

/// <summary>Result of <see cref="Services.ICimiimportService.GetToolInfoAsync"/>.</summary>
public sealed record CimiimportToolInfo(bool Found, string? Path, string? Version, string? Detail);
