namespace CimianStudio.ViewModels;

using System.Collections.ObjectModel;
using CimianStudio.Core.Models.Packages;
using CimianStudio.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// One developer bucket — vendor/author name + the packages they ship. Mirrors
/// <see cref="CategoryGroup"/>; kept as a separate type rather than a shared
/// "FacetGroup&lt;T&gt;" so each browser's labels stay self-documenting in XAML
/// data templates (no DataContext type-juggling).
/// </summary>
public sealed partial class DeveloperGroup : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<Package> Packages { get; init; } = [];

    public int Count => Packages.Count;
}

public sealed partial class DevelopersViewModel : ObservableObject
{
    /// <summary>Bucket label for packages with no <c>developer</c> field.</summary>
    public const string UnknownLabel = "(Unknown developer)";

    private readonly IPackageService _packageService;
    private readonly IRepositoryService _repositoryService;

    [ObservableProperty]
    public partial ObservableCollection<DeveloperGroup> Developers { get; set; } = [];

    [ObservableProperty]
    public partial DeveloperGroup? SelectedDeveloper { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public DevelopersViewModel(IPackageService packageService, IRepositoryService repositoryService)
    {
        ArgumentNullException.ThrowIfNull(packageService);
        ArgumentNullException.ThrowIfNull(repositoryService);
        _packageService = packageService;
        _repositoryService = repositoryService;
        _packageService.PackagesChanged += OnPackagesChanged;
    }

    public async Task LoadAsync()
    {
        if (_repositoryService.CurrentRepository is null)
        {
            Developers = [];
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var packages = await _packageService.GetAllPackagesAsync().ConfigureAwait(true);
            Developers = [.. BuildGroups(packages)];
            if (SelectedDeveloper is { } prev)
            {
                SelectedDeveloper = Developers
                    .FirstOrDefault(g => string.Equals(g.Name, prev.Name, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Developers = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static IEnumerable<DeveloperGroup> BuildGroups(IReadOnlyList<Package> packages)
    {
        var buckets = new SortedDictionary<string, List<Package>>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<Package>();

        foreach (var pkg in packages)
        {
            var key = pkg.Developer?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                unknown.Add(pkg);
                continue;
            }
            if (!buckets.TryGetValue(key, out var list))
            {
                list = [];
                buckets[key] = list;
            }
            list.Add(pkg);
        }

        foreach (var (name, items) in buckets)
        {
            yield return new DeveloperGroup
            {
                Name = name,
                Packages = [.. items.OrderBy(p => p.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)],
            };
        }
        if (unknown.Count > 0)
        {
            yield return new DeveloperGroup
            {
                Name = UnknownLabel,
                Packages = [.. unknown.OrderBy(p => p.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)],
            };
        }
    }

    private void OnPackagesChanged(object? sender, EventArgs e)
    {
        _ = LoadAsync();
    }
}
