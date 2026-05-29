namespace CimianStudio.ViewModels;

using System.Collections.ObjectModel;
using CimianStudio.Core.Models.Packages;
using CimianStudio.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// One category bucket — name + the packages tagged with it. Used as a
/// row in <c>CategoriesPage</c>'s sidebar list and as the source for its
/// detail pane.
/// </summary>
public sealed partial class CategoryGroup : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<Package> Packages { get; init; } = [];

    public int Count => Packages.Count;
}

public sealed partial class CategoriesViewModel : ObservableObject
{
    /// <summary>Bucket label for packages with no <c>category</c> field.</summary>
    public const string UncategorizedLabel = "(Uncategorized)";

    private readonly IPackageService _packageService;
    private readonly IRepositoryService _repositoryService;

    [ObservableProperty]
    public partial ObservableCollection<CategoryGroup> Categories { get; set; } = [];

    [ObservableProperty]
    public partial CategoryGroup? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public CategoriesViewModel(IPackageService packageService, IRepositoryService repositoryService)
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
            Categories = [];
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var packages = await _packageService.GetAllPackagesAsync().ConfigureAwait(true);
            Categories = [.. BuildGroups(packages)];
            // Preserve the user's selection when a reload (re-)materializes the
            // same set, so the detail pane doesn't jump after every refresh.
            if (SelectedCategory is { } prev)
            {
                SelectedCategory = Categories
                    .FirstOrDefault(g => string.Equals(g.Name, prev.Name, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Categories = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Groups packages by their <c>category</c> field. "(Uncategorized)" is
    /// always last so the alphabetical run on real categories stays clean.
    /// </summary>
    private static IEnumerable<CategoryGroup> BuildGroups(IReadOnlyList<Package> packages)
    {
        var buckets = new SortedDictionary<string, List<Package>>(StringComparer.OrdinalIgnoreCase);
        var uncategorized = new List<Package>();

        foreach (var pkg in packages)
        {
            var key = pkg.Category?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                uncategorized.Add(pkg);
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
            yield return new CategoryGroup
            {
                Name = name,
                Packages = [.. items.OrderBy(p => p.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)],
            };
        }
        if (uncategorized.Count > 0)
        {
            yield return new CategoryGroup
            {
                Name = UncategorizedLabel,
                Packages = [.. uncategorized.OrderBy(p => p.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)],
            };
        }
    }

    private void OnPackagesChanged(object? sender, EventArgs e)
    {
        _ = LoadAsync();
    }
}
