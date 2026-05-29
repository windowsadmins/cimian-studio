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
        SizeChanged += OnPageSizeChanged;
    }

    /// <summary>
    /// Pixel width below which the right section flows under the left section.
    /// Picked so that the 5-card rows still render legibly above the threshold
    /// — at the page's content width (window minus sidebar and our padding)
    /// each card gets roughly 110px above this point, which fits the longest
    /// labels ("Empty manifests", "Recent imports") without ellipsis.
    /// </summary>
    private const double TopSectionStackThreshold = 1180;

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyTopSectionLayout(e.NewSize.Width);
    }

    private void ApplyTopSectionLayout(double pageWidth)
    {
        if (RightSection is null || LeftSection is null)
        {
            return;
        }
        var stacked = pageWidth < TopSectionStackThreshold;
        // When stacked, each section spans both columns so it gets the full
        // page width — otherwise it'd still only occupy half. When side-by-
        // side, ColumnSpan=1 keeps each section in its own column.
        Grid.SetRow(RightSection, stacked ? 1 : 0);
        Grid.SetColumn(RightSection, stacked ? 0 : 1);
        Grid.SetColumnSpan(RightSection, stacked ? 2 : 1);
        Grid.SetColumnSpan(LeftSection, stacked ? 2 : 1);
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
        // Apply initial layout — SizeChanged hasn't necessarily fired yet if
        // the page was created at its final size.
        ApplyTopSectionLayout(ActualWidth);
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

        // Repo size = actual disk usage of pkgs/, not the sum of pkginfo
        // installer.size fields. pkginfo's installer.size is a metadata hint
        // that often lags reality (or is missing entirely on legacy entries);
        // for the dashboard "Repo size" tile the user expects truth-on-disk,
        // including the long-tail of files that aren't a primary installer
        // (subdirectory assets, supporting blobs). Offloaded to a worker
        // thread because the walk is bounded by FS metadata I/O.
        long repoBytes = await Task.Run(() => ComputeDirectoryBytes(repo.PkgsPath)).ConfigureAwait(true);

        // Largest single still uses pkginfo's installer.size — that's the
        // metadata that names a single installer payload, and matching it
        // back to a single on-disk file would need a per-package location
        // lookup we don't otherwise need here.
        long largestSingle = 0;
        foreach (var p in packages)
        {
            var size = p.Installer?.Size ?? 0;
            if (size > largestSingle) largestSingle = size;
        }

        // Reclaimable = sum of actual on-disk sizes for orphan packages'
        // installer locations. Falls back to installer.size if the location
        // is unknown or the file is missing.
        var orphanSet = new HashSet<string>(
            orphansAll.Select(p => p.Name ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        long reclaimable = await Task.Run(() => ComputeReclaimableBytes(packages, orphanSet, repo.PkgsPath)).ConfigureAwait(true);

        var iconCount = CountIcons(repo);
        var emptyManifests = manifests.Count(m =>
            (m.ManagedInstalls is null || m.ManagedInstalls.Count == 0)
            && (m.ManagedUpdates is null || m.ManagedUpdates.Count == 0)
            && (m.ManagedUninstalls is null || m.ManagedUninstalls.Count == 0)
            && (m.OptionalInstalls is null || m.OptionalInstalls.Count == 0)
            && (m.DefaultInstalls is null || m.DefaultInstalls.Count == 0)
            && (m.IncludedManifests is null || m.IncludedManifests.Count == 0));

        // Activity counts: walk a slice of recent commits and bucket by age +
        // import-subject heuristics. Best-effort — no git root means all zeros.
        var (commits24h, commits7d, imports24h, recentImports) = await ComputeActivityCountsAsync().ConfigureAwait(true);

        return new DashboardStats(
            PackageCount: packages.Count,
            ManifestCount: manifests.Count,
            CatalogCount: repo.CatalogCount,
            CategoryCount: categoryGroups.Count,
            DeveloperCount: developerGroups.Count,
            RepoBytes: repoBytes,
            ReclaimableBytes: reclaimable,
            OrphanCount: orphansAll.Count,
            LargestSingleBytes: largestSingle,
            IconCount: iconCount,
            NoIconCount: packages.Count(p => string.IsNullOrWhiteSpace(p.IconName)),
            NoCategoryCount: packages.Count(p => string.IsNullOrWhiteSpace(p.Category)),
            NoDeveloperCount: packages.Count(p => string.IsNullOrWhiteSpace(p.Developer)),
            NoDescriptionCount: packages.Count(p => string.IsNullOrWhiteSpace(p.Description)),
            NoInstallerCount: packages.Count(p => p.Installer is null),
            EmptyManifestCount: emptyManifests,
            UncommittedCount: _gitEntries.Count,
            Commits24hCount: commits24h,
            Commits7dCount: commits7d,
            Imports24hCount: imports24h,
            RecentImportsCount: recentImports);
    }

    /// <summary>
    /// Pulls a generous slice of commit history once and derives four counts
    /// from it: total commits in the last 24h / 7d, plus the subset whose
    /// subject reads like an import (matches <see cref="ImportSubjectRegex"/>).
    /// </summary>
    private async Task<(int Commits24h, int Commits7d, int Imports24h, int RecentImports)> ComputeActivityCountsAsync()
    {
        if (_gitInfo is null)
        {
            return (0, 0, 0, 0);
        }
        IReadOnlyList<GitCommit> history;
        try
        {
            history = await _gitService.GetHistoryAsync(_gitInfo, limit: 500).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return (0, 0, 0, 0);
        }

        var now = DateTimeOffset.UtcNow;
        var day = now.AddDays(-1);
        var week = now.AddDays(-7);
        var c24 = 0;
        var c7 = 0;
        var i24 = 0;
        var iRecent = 0;
        foreach (var c in history)
        {
            var w = c.When.ToUniversalTime();
            var isImport = ImportSubjectRegex.IsMatch(c.Subject ?? string.Empty);
            if (w >= day)
            {
                c24++;
                if (isImport) i24++;
            }
            if (w >= week)
            {
                c7++;
                if (isImport) iRecent++;
            }
        }
        return (c24, c7, i24, iRecent);
    }

    /// <summary>
    /// Identifies commits authored by the import wizard. Matches both the
    /// CLI's own messages (<c>cimiimport: …</c>) and the more generic
    /// <c>import: …</c> / <c>imports …</c> patterns the wizard's auto-message
    /// uses. Case-insensitive.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ImportSubjectRegex =
        new(@"^\s*(cimiimport|import|imports?)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Sums the size of every file under <paramref name="root"/> recursively.
    /// Returns 0 if the directory doesn't exist; swallows per-file I/O errors
    /// (permission denied on a single path shouldn't bomb the whole dashboard).
    /// </summary>
    private static long ComputeDirectoryBytes(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return 0;
        }
        long total = 0;
        try
        {
            // EnumerationOptions with IgnoreInaccessible so we don't bail on
            // a single locked / permission-denied path partway through.
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var path in Directory.EnumerateFiles(root, "*", opts))
            {
                try
                {
                    total += new FileInfo(path).Length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>
    /// For each orphan package, look up its installer's on-disk size
    /// (<see cref="Installer.Location"/> joined under pkgs/). Falls back to
    /// pkginfo's <c>installer.size</c> when the on-disk lookup fails so a
    /// missing-from-disk orphan still contributes a sensible estimate.
    /// </summary>
    private static long ComputeReclaimableBytes(
        IReadOnlyList<Package> packages,
        HashSet<string> orphanNames,
        string pkgsRoot)
    {
        long total = 0;
        foreach (var p in packages)
        {
            if (string.IsNullOrEmpty(p.Name) || !orphanNames.Contains(p.Name))
            {
                continue;
            }

            var location = p.Installer?.Location;
            var added = false;
            if (!string.IsNullOrEmpty(location) && !string.IsNullOrEmpty(pkgsRoot))
            {
                try
                {
                    // Guard against locations like `..\..\somefile` or absolute paths
                    // that escape the repo's pkgs/ root and would pull external file
                    // sizes into the reclaimable-space estimate.
                    var fullPath = Path.GetFullPath(Path.Combine(pkgsRoot, location));
                    var canonicalRoot = Path.GetFullPath(pkgsRoot)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    if (fullPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(fullPath))
                    {
                        total += new FileInfo(fullPath).Length;
                        added = true;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
            if (!added)
            {
                total += p.Installer?.Size ?? 0;
            }
        }
        return total;
    }

    private static int CountIcons(CimianRepository repo)
    {
        var dir = repo.IconsPath;
        if (!Directory.Exists(dir)) return 0;
        string[] exts = [".png", ".jpg", ".jpeg", ".icns"];

        // EnumerateFiles with AllDirectories aborts on the first inaccessible
        // subdirectory, which can fail the entire dashboard load for one
        // protected folder under icons/. Walk manually so a single restricted
        // child folder is skipped instead.
        var count = 0;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    if (exts.Any(e => string.Equals(Path.GetExtension(file), e, StringComparison.OrdinalIgnoreCase)))
                    {
                        count++;
                    }
                }
                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    stack.Push(sub);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return count;
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

    private static string StripVersionPin(string name)
    {
        // Walk from the right looking for `-<digit>` — only the rightmost such
        // boundary terminates a version pin. This keeps `firefox-esr` intact
        // while still trimming `firefox-esr-128.4.0` to `firefox-esr`.
        var i = name.Length - 1;
        while (i > 0)
        {
            var dash = name.LastIndexOf('-', i);
            if (dash <= 0 || dash + 1 >= name.Length) return name;
            var next = name[dash + 1];
            if (char.IsDigit(next))
            {
                return name[..dash];
            }
            i = dash - 1;
        }
        return name;
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
                // Only strip when the suffix starts with a digit — real package
                // names like `firefox-esr` or `dotnet-sdk-9` include hyphens
                // and must not be truncated to the first segment.
                sink.Add(StripVersionPin(s));
            }
        }
    }

    /// <summary>
    /// Splits a metric-card value into its numeric prefix and any trailing
    /// alphabetic unit ("239.3" + "GB"). Returns (value, null) when the
    /// string is a plain number with no unit suffix.
    /// </summary>
    private static (string Number, string? Unit) SplitNumberAndUnit(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return (value, null);
        }
        var i = 0;
        while (i < value.Length && (char.IsDigit(value[i]) || value[i] == '.' || value[i] == ','))
        {
            i++;
        }
        if (i == 0 || i == value.Length)
        {
            return (value, null);
        }
        return (value[..i], value[i..]);
    }

    /// <summary>
    /// Formats a byte count compactly: "239.3GB", "380.1MB", no space between
    /// the number and the unit so the value reads as a single chip on the
    /// dashboard cards. Plain bytes drop the unit entirely ("0" not "0 B").
    /// </summary>
    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes}B");
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = 0;
        value /= 1024;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#}{units[unit]}");
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
    /// Populates the four metric rows on the dashboard. Each row is a Grid
    /// with five <c>*</c>-width columns so cards always share the available
    /// width equally (and every card across the four rows is the same size).
    /// </summary>
    private void BuildStatCards(CimianRepository repo, DashboardStats stats)
    {
        FillGridRow(StatsRowA, [
            ("Packages", stats.PackageCount.ToString(CultureInfo.InvariantCulture), "packages"),
            ("Manifests", stats.ManifestCount.ToString(CultureInfo.InvariantCulture), "manifests"),
            ("Catalogs", stats.CatalogCount.ToString(CultureInfo.InvariantCulture), "catalogs"),
            ("Categories", stats.CategoryCount.ToString(CultureInfo.InvariantCulture), "categories"),
            ("Developers", stats.DeveloperCount.ToString(CultureInfo.InvariantCulture), "developers"),
        ]);

        FillGridRow(StatsRowB, [
            ("Repo size", FormatByteSize(stats.RepoBytes), null),
            ("Reclaimable", FormatByteSize(stats.ReclaimableBytes), null),
            ("Orphan packages", stats.OrphanCount.ToString(CultureInfo.InvariantCulture), null),
            ("Largest single", FormatByteSize(stats.LargestSingleBytes), null),
            ("Icons", stats.IconCount.ToString(CultureInfo.InvariantCulture), "icons"),
        ]);

        FillGridRow(HealthRow, [
            ("No icon", stats.NoIconCount.ToString(CultureInfo.InvariantCulture), null),
            ("No category", stats.NoCategoryCount.ToString(CultureInfo.InvariantCulture), null),
            ("No developer", stats.NoDeveloperCount.ToString(CultureInfo.InvariantCulture), null),
            ("No description", stats.NoDescriptionCount.ToString(CultureInfo.InvariantCulture), null),
            ("No installer", stats.NoInstallerCount.ToString(CultureInfo.InvariantCulture), null),
            ("Empty manifests", stats.EmptyManifestCount.ToString(CultureInfo.InvariantCulture), null),
        ]);

        FillGridRow(ActivityRow, [
            ("Uncommitted", stats.UncommittedCount.ToString(CultureInfo.InvariantCulture), "git"),
            ("Commits (24h)", stats.Commits24hCount.ToString(CultureInfo.InvariantCulture), "git"),
            ("Commits (7d)", stats.Commits7dCount.ToString(CultureInfo.InvariantCulture), "git"),
            ("Imports (24h)", stats.Imports24hCount.ToString(CultureInfo.InvariantCulture), "git"),
            ("Recent imports", stats.RecentImportsCount.ToString(CultureInfo.InvariantCulture), "git"),
        ]);
    }

    private static void FillGridRow(Grid row, (string Label, string Value, string? NavTag)[] cards)
    {
        row.Children.Clear();
        for (var i = 0; i < cards.Length; i++)
        {
            var card = BuildCard(cards[i].Label, cards[i].Value, cards[i].NavTag);
            Grid.SetColumn(card, i);
            row.Children.Add(card);
        }
    }

    /// <summary>Compact metric card. Clickable when <paramref name="navTag"/> is set.</summary>
    private static Border BuildCard(string label, string value, string? navTag)
    {
        var stack = new StackPanel { Spacing = 2 };
        // CaptionTextBlockStyle already carries the secondary-emphasis brush
        // via ThemeResource. Don't override Foreground here — looking up
        // TextFillColorSecondaryBrush off Application.Resources resolves
        // statically, so it ends up bound to whatever brush was current at
        // app start, which then renders invisibly after a theme switch.
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        // Value text. The number portion uses Title size; if the string ends
        // in a byte-size unit (KB/MB/GB/TB/B), the unit is split off into a
        // smaller Run so "239.3GB" reads as a big "239.3" next to a compact
        // "GB" badge, instead of the whole string scaling huge.
        var valueText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        var (numberPart, unitPart) = SplitNumberAndUnit(value);
        if (unitPart is null)
        {
            valueText.Text = numberPart;
        }
        else
        {
            valueText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = numberPart });
            // Thin space (U+2009) between number and unit. Same FontSize as
            // the unit so the gap doesn't render at the larger number size.
            valueText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = " ",
                FontSize = 14,
            });
            valueText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = unitPart,
                FontSize = 14,
            });
        }
        stack.Children.Add(valueText);

        // No MinWidth: cards live inside 5-column Grids with `Width="*"`, so
        // they share the row's available width equally. Letting them shrink
        // freely is what makes the dashboard responsive to window resize
        // without ever clipping content off-screen.
        var card = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
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
        long ReclaimableBytes,
        int OrphanCount,
        long LargestSingleBytes,
        int IconCount,
        int NoIconCount,
        int NoCategoryCount,
        int NoDeveloperCount,
        int NoDescriptionCount,
        int NoInstallerCount,
        int EmptyManifestCount,
        int UncommittedCount,
        int Commits24hCount,
        int Commits7dCount,
        int Imports24hCount,
        int RecentImportsCount);

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
