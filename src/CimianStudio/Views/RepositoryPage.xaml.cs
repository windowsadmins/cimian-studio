namespace CimianStudio.Views;

using System.Globalization;
using CimianStudio.Core.Models.Git;
using CimianStudio.Core.Models.Manifests;
using CimianStudio.Core.Models.Packages;
using CimianStudio.Core.Models.Repository;
using CimianStudio.Core.Models.Search;
using CimianStudio.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

public sealed partial class RepositoryPage : Page
{
    private const int RecentLimit = 10;
    private const int SearchDebounceMs = 150;
    private const int SearchMaxResults = 50;

    private readonly IRepositoryService _repositoryService;
    private readonly IPackageService _packageService;
    private readonly IManifestService _manifestService;
    private readonly IGitService _gitService;
    private readonly ISearchService _searchService;
    private readonly DispatcherQueue _dispatcher;

    private List<Package> _recentPackages = [];
    private List<Manifest> _recentManifests = [];
    private GitRepositoryInfo? _gitInfo;
    private List<GitStatusEntry> _gitEntries = [];
    private List<Package> _orphanPackages = [];
    private List<Package> _largestPackages = [];

    private int _searchEpoch;
    private List<SearchHit> _currentHits = [];

    public RepositoryPage(
        IRepositoryService repositoryService,
        IPackageService packageService,
        IManifestService manifestService,
        IGitService gitService,
        ISearchService searchService)
    {
        ArgumentNullException.ThrowIfNull(repositoryService);
        ArgumentNullException.ThrowIfNull(packageService);
        ArgumentNullException.ThrowIfNull(manifestService);
        ArgumentNullException.ThrowIfNull(gitService);
        ArgumentNullException.ThrowIfNull(searchService);
        _repositoryService = repositoryService;
        _packageService = packageService;
        _manifestService = manifestService;
        _gitService = gitService;
        _searchService = searchService;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var repo = _repositoryService.CurrentRepository;
        if (repo is null)
        {
            RepoNameText.Text = "No repository";
            RepoPathText.Text = string.Empty;
            return;
        }

        RepoNameText.Text = repo.Name;
        RepoPathText.Text = repo.RootPath;
        _searchService.ProgressChanged += OnSearchProgress;
        UpdateIndexingPill(_searchService.IsReady ? null : new SearchIndexProgress(0, 0, false));
        await LoadRecentsAsync().ConfigureAwait(true);
        await LoadGitStatusAsync(repo).ConfigureAwait(true);
        // Insights computes the per-package facets and stashes the orphan /
        // largest lists used both for the leaderboard lists below and the
        // matching stat-row counts. Run before BuildStatCards.
        var stats = await LoadInsightsAsync(repo).ConfigureAwait(true);
        BuildStatCards(repo, stats);
        await LoadRecentCommitsAsync().ConfigureAwait(true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _searchService.ProgressChanged -= OnSearchProgress;
        Interlocked.Increment(ref _searchEpoch);
    }

    private void OnSearchProgress(object? sender, SearchIndexProgress progress)
    {
        _dispatcher.TryEnqueue(() => UpdateIndexingPill(progress));
    }

    private void UpdateIndexingPill(SearchIndexProgress? progress)
    {
        if (progress is null || progress.IsComplete)
        {
            IndexingPill.Visibility = Visibility.Collapsed;
            return;
        }
        IndexingPill.Visibility = Visibility.Visible;
        IndexingPillText.Text = progress.Total > 0
            ? string.Create(CultureInfo.InvariantCulture, $"indexing {progress.Indexed}/{progress.Total}…")
            : "indexing…";
    }

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var epoch = Interlocked.Increment(ref _searchEpoch);
        var query = SearchBox.Text;

        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResultsCard.Visibility = Visibility.Collapsed;
            _currentHits = [];
            return;
        }

        await Task.Delay(SearchDebounceMs).ConfigureAwait(true);
        if (epoch != Volatile.Read(ref _searchEpoch)) return;

        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = await _searchService.SearchAsync(query, SearchMaxResults).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (epoch != Volatile.Read(ref _searchEpoch)) return;
        ShowResults(query, hits);
    }

    private void ShowResults(string query, IReadOnlyList<SearchHit> hits)
    {
        _currentHits = [.. hits];
        SearchResultsCard.Visibility = Visibility.Visible;
        SearchResultsHeader.Text = string.Create(CultureInfo.InvariantCulture, $"Results for “{query.Trim()}”");
        SearchResultsCount.Text = hits.Count switch
        {
            0 => "no matches",
            SearchMaxResults => string.Create(CultureInfo.InvariantCulture, $"showing first {SearchMaxResults}"),
            1 => "1 match",
            _ => string.Create(CultureInfo.InvariantCulture, $"{hits.Count} matches"),
        };
        SearchResultsList.ItemsSource = hits.Select(BuildResultRow).ToList();
    }

    private static FrameworkElement BuildResultRow(SearchHit hit)
    {
        var stack = new StackPanel { Spacing = 2 };
        var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerStack.Children.Add(new TextBlock
        {
            Text = hit.DisplayName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = hit.Kind == SearchHitKind.Package ? "package" : "manifest",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = string.Create(CultureInfo.InvariantCulture, $"line {hit.LineNumber}"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(headerStack);
        stack.Children.Add(new TextBlock
        {
            Text = hit.Snippet,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return new Border
        {
            Padding = new Thickness(8, 6, 8, 6),
            Child = stack,
        };
    }

    private async void OnSearchResultClicked(object sender, ItemClickEventArgs e)
    {
        if (sender is not ListView view) return;
        var index = view.Items.IndexOf(e.ClickedItem);
        if (index < 0 || index >= _currentHits.Count) return;
        await OpenHitAsync(_currentHits[index]).ConfigureAwait(true);
    }

    private async Task OpenHitAsync(SearchHit hit)
    {
        if (App.MainWindowInstance is not { } window) return;
        var normalized = System.IO.Path.GetFullPath(hit.AbsolutePath);

        if (hit.Kind == SearchHitKind.Package)
        {
            try
            {
                var packages = await _packageService.GetAllPackagesAsync().ConfigureAwait(true);
                var match = packages.FirstOrDefault(p =>
                    !string.IsNullOrEmpty(p.FilePath) &&
                    string.Equals(System.IO.Path.GetFullPath(p.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
                if (match is not null) window.NavigateToPackage(match);
            }
            catch { }
        }
        else
        {
            try
            {
                var manifests = await _manifestService.GetAllManifestsAsync().ConfigureAwait(true);
                var match = manifests.FirstOrDefault(m =>
                    !string.IsNullOrEmpty(m.FilePath) &&
                    string.Equals(System.IO.Path.GetFullPath(m.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
                if (match is not null) window.NavigateToManifest(match);
            }
            catch { }
        }
    }

    private async Task LoadGitStatusAsync(CimianRepository repo)
    {
        try
        {
            _gitInfo = await _gitService.DiscoverAsync(repo.RootPath).ConfigureAwait(true);
        }
        catch
        {
            _gitInfo = null;
        }

        if (_gitInfo is null)
        {
            GitStatusCard.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var entries = await _gitService.GetStatusAsync(_gitInfo).ConfigureAwait(true);
            _gitEntries = [.. entries];
        }
        catch
        {
            _gitEntries = [];
        }

        GitStatusCard.Visibility = Visibility.Visible;
        GitStatusTitle.Text = _gitEntries.Count == 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"Git · {_gitInfo.Branch ?? "detached"}  ·  Working tree clean")
            : string.Create(CultureInfo.InvariantCulture,
                $"Git · {_gitInfo.Branch ?? "detached"}  ·  {_gitEntries.Count} change(s) pending");

        var rootName = System.IO.Path.GetFileName(
            _gitInfo.GitRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        GitStatusScope.Text = string.IsNullOrEmpty(_gitInfo.RelativeRepoPath)
            ? $"Scoped to {rootName}"
            : $"Scoped to {_gitInfo.RelativeRepoPath} (in {rootName})";
    }

    private void OnOpenGitTabClicked(object sender, RoutedEventArgs e)
    {
        App.MainWindowInstance?.NavigateTo("git");
    }

    private async Task LoadRecentsAsync()
    {
        try
        {
            var packages = await _packageService.GetAllPackagesAsync().ConfigureAwait(true);
            _recentPackages = [.. packages
                .Where(p => p.LastModified.HasValue)
                .OrderByDescending(p => p.LastModified)
                .Take(RecentLimit)];

            var manifests = await _manifestService.GetAllManifestsAsync().ConfigureAwait(true);
            _recentManifests = [.. manifests
                .Where(m => m.LastModified.HasValue)
                .OrderByDescending(m => m.LastModified)
                .Take(RecentLimit)];
        }
        catch (Exception)
        {
            _recentPackages = [];
            _recentManifests = [];
        }

        RecentPackagesList.ItemsSource = _recentPackages
            .Select(p => string.IsNullOrEmpty(p.Version) ? p.Name : $"{p.Name}  ({p.Version})")
            .ToList();
        NoRecentPackagesText.Visibility = _recentPackages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentPackagesList.Visibility = _recentPackages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        RecentManifestsList.ItemsSource = _recentManifests.Select(m => m.Name ?? string.Empty).ToList();
        NoRecentManifestsText.Visibility = _recentManifests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentManifestsList.Visibility = _recentManifests.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRecentPackageClicked(object sender, ItemClickEventArgs e)
    {
        // Map the clicked label back to its index in _recentPackages — string lookup
        // would be fragile because of the "  (version)" suffix.
        var view = sender as ListView;
        var index = view?.Items.IndexOf(e.ClickedItem) ?? -1;
        if (index < 0 || index >= _recentPackages.Count) return;
        var pkg = _recentPackages[index];
        if (App.MainWindowInstance is { } window)
        {
            window.NavigateToPackage(pkg);
        }
    }

    private void OnRecentManifestClicked(object sender, ItemClickEventArgs e)
    {
        var view = sender as ListView;
        var index = view?.Items.IndexOf(e.ClickedItem) ?? -1;
        if (index < 0 || index >= _recentManifests.Count) return;
        var manifest = _recentManifests[index];
        if (App.MainWindowInstance is { } window)
        {
            window.NavigateToManifest(manifest);
        }
    }

    private const int InsightsTopN = 10;

    /// <summary>
    /// Populates the leaderboard panels (orphans, largest, top categories,
    /// top developers) and returns an aggregate <see cref="DashboardStats"/>
    /// snapshot used to fill the metric rows. Best-effort — failures collapse
    /// to empty hints rather than breaking the page.
    /// </summary>
    private async Task<DashboardStats> LoadInsightsAsync(CimianRepository repo)
    {
        IReadOnlyList<Package> packages;
        IReadOnlyList<Manifest> manifests;
        try
        {
            packages = await _packageService.GetAllPackagesAsync().ConfigureAwait(true);
            manifests = await _manifestService.GetAllManifestsAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            packages = [];
            manifests = [];
        }

        // Orphans
        var referenced = CollectReferencedPackageNames(manifests);
        var orphansAll = packages
            .Where(p => !string.IsNullOrEmpty(p.Name) && !referenced.Contains(p.Name))
            .ToList();
        _orphanPackages = [.. orphansAll
            .OrderBy(p => p.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(InsightsTopN)];

        var orphanRows = _orphanPackages
            .Select(p => string.IsNullOrEmpty(p.Version) ? p.EffectiveDisplayName : $"{p.EffectiveDisplayName}  ({p.Version})")
            .ToList();
        OrphansList.ItemsSource = orphanRows;
        OrphansList.Visibility = orphanRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        OrphansEmpty.Visibility = orphanRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OrphansCountText.Text = string.Create(CultureInfo.InvariantCulture, $"({orphansAll.Count})");

        // Largest
        _largestPackages = [.. packages
            .Where(p => p.Installer is { Size: > 0 })
            .OrderByDescending(p => p.Installer!.Size ?? 0)
            .Take(InsightsTopN)];
        var largestRows = _largestPackages
            .Select(p => $"{p.EffectiveDisplayName}  ·  {FormatByteSize(p.Installer!.Size ?? 0)}")
            .ToList();
        LargestList.ItemsSource = largestRows;
        LargestList.Visibility = largestRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        LargestEmpty.Visibility = largestRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Top categories / developers — drop (Uncategorized) / (Unknown).
        var categoryGroups = packages
            .Where(p => !string.IsNullOrWhiteSpace(p.Category))
            .GroupBy(p => p.Category!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var topCategories = categoryGroups
            .Take(InsightsTopN)
            .Select(t => $"{t.Name}  ·  {t.Count}")
            .ToList();
        TopCategoriesList.ItemsSource = topCategories;
        TopCategoriesList.Visibility = topCategories.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TopCategoriesEmpty.Visibility = topCategories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var developerGroups = packages
            .Where(p => !string.IsNullOrWhiteSpace(p.Developer))
            .GroupBy(p => p.Developer!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var topDevelopers = developerGroups
            .Take(InsightsTopN)
            .Select(t => $"{t.Name}  ·  {t.Count}")
            .ToList();
        TopDevelopersList.ItemsSource = topDevelopers;
        TopDevelopersList.Visibility = topDevelopers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TopDevelopersEmpty.Visibility = topDevelopers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Aggregates for the stat-row cards. Repo bytes = sum of installer sizes
        // we actually know about; missing/zero sizes don't contribute. Largest
        // single = the biggest installer.Size in the set.
        long repoBytes = 0;
        long largestSingle = 0;
        foreach (var p in packages)
        {
            var size = p.Installer?.Size ?? 0;
            if (size > 0)
            {
                repoBytes += size;
                if (size > largestSingle) largestSingle = size;
            }
        }

        var iconCount = CountIcons(repo);
        var emptyManifests = manifests.Count(m =>
            (m.ManagedInstalls is null || m.ManagedInstalls.Count == 0)
            && (m.ManagedUpdates is null || m.ManagedUpdates.Count == 0)
            && (m.ManagedUninstalls is null || m.ManagedUninstalls.Count == 0)
            && (m.OptionalInstalls is null || m.OptionalInstalls.Count == 0)
            && (m.DefaultInstalls is null || m.DefaultInstalls.Count == 0)
            && (m.IncludedManifests is null || m.IncludedManifests.Count == 0));

        return new DashboardStats(
            PackageCount: packages.Count,
            ManifestCount: manifests.Count,
            CatalogCount: repo.CatalogCount,
            CategoryCount: categoryGroups.Count,
            DeveloperCount: developerGroups.Count,
            RepoBytes: repoBytes,
            OrphanCount: orphansAll.Count,
            LargestSingleBytes: largestSingle,
            IconCount: iconCount,
            UncommittedCount: _gitEntries.Count,
            NoDescriptionCount: packages.Count(p => string.IsNullOrWhiteSpace(p.Description)),
            NoCategoryCount: packages.Count(p => string.IsNullOrWhiteSpace(p.Category)),
            NoDeveloperCount: packages.Count(p => string.IsNullOrWhiteSpace(p.Developer)),
            NoInstallerCount: packages.Count(p => p.Installer is null),
            EmptyManifestCount: emptyManifests);
    }

    private static int CountIcons(CimianRepository repo)
    {
        var dir = repo.IconsPath;
        if (!Directory.Exists(dir)) return 0;
        try
        {
            string[] exts = [".png", ".jpg", ".jpeg", ".icns"];
            return Directory
                .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Count(f => exts.Any(e => string.Equals(Path.GetExtension(f), e, StringComparison.OrdinalIgnoreCase)));
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private List<GitCommit> _recentCommits = [];

    /// <summary>
    /// Pulls the most recent 5 commits via IGitService.GetHistoryAsync.
    /// Hides the panel if no git root is present or the history is empty.
    /// </summary>
    private async Task LoadRecentCommitsAsync()
    {
        if (_gitInfo is null)
        {
            RecentCommitsCard.Visibility = Visibility.Collapsed;
            return;
        }

        IReadOnlyList<GitCommit> commits;
        try
        {
            commits = await _gitService.GetHistoryAsync(_gitInfo, limit: 5).ConfigureAwait(true);
        }
        catch (Exception)
        {
            RecentCommitsCard.Visibility = Visibility.Collapsed;
            return;
        }

        _recentCommits = [.. commits];
        if (_recentCommits.Count == 0)
        {
            RecentCommitsCard.Visibility = Visibility.Collapsed;
            return;
        }

        RecentCommitsCard.Visibility = Visibility.Visible;
        RecentCommitsBranchText.Text = string.IsNullOrEmpty(_gitInfo.Branch)
            ? string.Empty
            : "on " + _gitInfo.Branch;
        RecentCommitsList.ItemsSource = _recentCommits
            .Select(c => $"{c.Sha[..Math.Min(7, c.Sha.Length)]}   {c.Subject}   ·   {FormatRelativeTime(c.When)}")
            .ToList();
    }

    private void OnRecentCommitClicked(object sender, ItemClickEventArgs e)
    {
        // For now, jump to the Git tab; future PRs can deep-link to the commit.
        App.MainWindowInstance?.NavigateTo("git");
    }

    private static string FormatRelativeTime(DateTimeOffset when)
    {
        var delta = DateTimeOffset.UtcNow - when.ToUniversalTime();
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalMinutes}m");
        if (delta.TotalHours < 24) return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalHours}h");
        if (delta.TotalDays < 30) return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalDays}d");
        return when.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static HashSet<string> CollectReferencedPackageNames(IReadOnlyList<Manifest> manifests)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            AddAll(names, manifest.ManagedInstalls);
            AddAll(names, manifest.ManagedUpdates);
            AddAll(names, manifest.ManagedUninstalls);
            AddAll(names, manifest.OptionalInstalls);
            AddAll(names, manifest.DefaultInstalls);

            if (manifest.ConditionalItems is { } conditionals)
            {
                WalkConditionals(names, conditionals);
            }
        }
        return names;
    }

    private static void WalkConditionals(HashSet<string> sink, IEnumerable<ConditionalItem> items)
    {
        foreach (var item in items)
        {
            AddAll(sink, item.ManagedInstalls);
            AddAll(sink, item.ManagedUpdates);
            AddAll(sink, item.ManagedUninstalls);
            AddAll(sink, item.OptionalInstalls);
            if (item.NestedConditionalItems is { } nested)
            {
                WalkConditionals(sink, nested);
            }
        }
    }

    private static void AddAll(HashSet<string> sink, IEnumerable<string>? source)
    {
        if (source is null) return;
        foreach (var s in source)
        {
            if (!string.IsNullOrWhiteSpace(s))
            {
                // Manifests can pin a specific version with `name-1.2.3`; strip
                // the version suffix so it matches the pkginfo's `name` field.
                var dash = s.IndexOf('-', StringComparison.Ordinal);
                sink.Add(dash > 0 ? s[..dash] : s);
            }
        }
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = 0;
        value /= 1024;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    private void OnOrphanClicked(object sender, ItemClickEventArgs e)
    {
        var view = sender as ListView;
        var index = view?.Items.IndexOf(e.ClickedItem) ?? -1;
        if (index < 0 || index >= _orphanPackages.Count) return;
        if (App.MainWindowInstance is { } window)
        {
            window.NavigateToPackage(_orphanPackages[index]);
        }
    }

    private void OnLargestClicked(object sender, ItemClickEventArgs e)
    {
        var view = sender as ListView;
        var index = view?.Items.IndexOf(e.ClickedItem) ?? -1;
        if (index < 0 || index >= _largestPackages.Count) return;
        if (App.MainWindowInstance is { } window)
        {
            window.NavigateToPackage(_largestPackages[index]);
        }
    }

    /// <summary>
    /// Populates the three metric rows on the dashboard. Stats row A is the
    /// top-line counts (Packages / Manifests / Catalogs / Categories /
    /// Developers); row B mixes filesystem and git facts (Repo size / Orphan
    /// packages / Largest single / Uncommitted / Recent commits); the health
    /// row counts pkginfo records missing required-feeling fields. Each card
    /// is clickable where a destination page exists.
    /// </summary>
    private void BuildStatCards(CimianRepository repo, DashboardStats stats)
    {
        StatsRowA.Items.Clear();
        StatsRowA.Items.Add(BuildCard("Packages", stats.PackageCount.ToString(CultureInfo.InvariantCulture), navTag: "packages"));
        StatsRowA.Items.Add(BuildCard("Manifests", stats.ManifestCount.ToString(CultureInfo.InvariantCulture), navTag: "manifests"));
        StatsRowA.Items.Add(BuildCard("Catalogs", stats.CatalogCount.ToString(CultureInfo.InvariantCulture), navTag: "catalogs"));
        StatsRowA.Items.Add(BuildCard("Categories", stats.CategoryCount.ToString(CultureInfo.InvariantCulture), navTag: "categories"));
        StatsRowA.Items.Add(BuildCard("Developers", stats.DeveloperCount.ToString(CultureInfo.InvariantCulture), navTag: "developers"));

        StatsRowB.Items.Clear();
        StatsRowB.Items.Add(BuildCard("Repo size", FormatByteSize(stats.RepoBytes), navTag: null));
        StatsRowB.Items.Add(BuildCard("Orphan packages", stats.OrphanCount.ToString(CultureInfo.InvariantCulture), navTag: null));
        StatsRowB.Items.Add(BuildCard("Largest single", FormatByteSize(stats.LargestSingleBytes), navTag: null));
        StatsRowB.Items.Add(BuildCard("Icons", stats.IconCount.ToString(CultureInfo.InvariantCulture), navTag: "icons"));
        StatsRowB.Items.Add(BuildCard("Uncommitted", stats.UncommittedCount.ToString(CultureInfo.InvariantCulture), navTag: "git"));

        HealthRow.Items.Clear();
        HealthRow.Items.Add(BuildCard("No description", stats.NoDescriptionCount.ToString(CultureInfo.InvariantCulture), navTag: null));
        HealthRow.Items.Add(BuildCard("No category", stats.NoCategoryCount.ToString(CultureInfo.InvariantCulture), navTag: null));
        HealthRow.Items.Add(BuildCard("No developer", stats.NoDeveloperCount.ToString(CultureInfo.InvariantCulture), navTag: null));
        HealthRow.Items.Add(BuildCard("No installer", stats.NoInstallerCount.ToString(CultureInfo.InvariantCulture), navTag: null));
        HealthRow.Items.Add(BuildCard("Empty manifests", stats.EmptyManifestCount.ToString(CultureInfo.InvariantCulture), navTag: null));
    }

    /// <summary>Compact metric card. Clickable when <paramref name="navTag"/> is set.</summary>
    private static Border BuildCard(string label, string value, string? navTag)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });

        var card = new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            MinWidth = 148,
            Child = stack,
            Style = (Style)Application.Current.Resources["CardStyle"],
            Translation = new System.Numerics.Vector3(0, 0, 16),
        };
        if (Application.Current.Resources["CardShadow"] is Microsoft.UI.Xaml.Media.Shadow shadow)
        {
            card.Shadow = shadow;
        }

        if (navTag is not null)
        {
            // Hover affordance via cursor + tap-to-navigate. Keeps the card a
            // plain Border instead of a Button so the typography / shadow of
            // every card stays consistent.
            card.Tag = navTag;
            ToolTipService.SetToolTip(card, $"Open {navTag} tab");
            card.PointerEntered += (s, _) => ((Border)s).Opacity = 0.85;
            card.PointerExited += (s, _) => ((Border)s).Opacity = 1.0;
            card.Tapped += (s, _) =>
            {
                if (s is Border b && b.Tag is string tag)
                {
                    App.MainWindowInstance?.NavigateTo(tag);
                }
            };
        }
        return card;
    }

    /// <summary>One pass of aggregate stats for the dashboard rows.</summary>
    private sealed record DashboardStats(
        int PackageCount,
        int ManifestCount,
        int CatalogCount,
        int CategoryCount,
        int DeveloperCount,
        long RepoBytes,
        int OrphanCount,
        long LargestSingleBytes,
        int IconCount,
        int UncommittedCount,
        int NoDescriptionCount,
        int NoCategoryCount,
        int NoDeveloperCount,
        int NoInstallerCount,
        int EmptyManifestCount);

    private async void OnValidateClicked(object sender, RoutedEventArgs e)
    {
        var repo = _repositoryService.CurrentRepository;
        if (repo is null)
        {
            return;
        }

        var result = await _repositoryService.ValidateRepositoryAsync(repo).ConfigureAwait(true);

        ValidationBar.IsOpen = true;
        if (result.Errors.Count > 0)
        {
            ValidationBar.Severity = InfoBarSeverity.Error;
            ValidationBar.Title = "Repository has errors";
            ValidationBar.Message = $"{result.Errors.Count} error(s), {result.Warnings.Count} warning(s).";
        }
        else if (result.Warnings.Count > 0)
        {
            ValidationBar.Severity = InfoBarSeverity.Warning;
            ValidationBar.Title = "Repository is valid with warnings";
            ValidationBar.Message = $"{result.Warnings.Count} warning(s).";
        }
        else
        {
            ValidationBar.Severity = InfoBarSeverity.Success;
            ValidationBar.Title = "Repository looks good";
            ValidationBar.Message = "All required directories are present.";
        }

        var details = new List<string>();
        details.AddRange(result.Errors.Select(static e => "Error: " + e));
        details.AddRange(result.Warnings.Select(static w => "Warning: " + w));
        ValidationDetails.ItemsSource = details;
    }
}
