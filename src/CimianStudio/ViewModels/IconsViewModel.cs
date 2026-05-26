namespace CimianStudio.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using CimianStudio.Core.Models.Icons;
using CimianStudio.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

public sealed partial class IconsViewModel : ObservableObject
{
    private readonly IIconService _iconService;
    private readonly IRepositoryService _repositoryService;

    [ObservableProperty]
    public partial ObservableCollection<IconAsset> Icons { get; set; } = [];

    [ObservableProperty]
    public partial IconAsset? SelectedIcon { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRebuild))]
    public partial bool IsRebuilding { get; set; }

    public bool CanRebuild => !IsRebuilding;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public IconsViewModel(IIconService iconService, IRepositoryService repositoryService)
    {
        ArgumentNullException.ThrowIfNull(iconService);
        ArgumentNullException.ThrowIfNull(repositoryService);
        _iconService = iconService;
        _repositoryService = repositoryService;
        _iconService.IconHashesChanged += OnIconHashesChanged;
    }

    public async Task LoadAsync()
    {
        if (_repositoryService.CurrentRepository is null)
        {
            Icons = [];
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var loaded = await _iconService.ListAsync().ConfigureAwait(true);
            Icons = [.. loaded];
            // Preserve selection across reloads when the same filename is still
            // present — otherwise the detail pane jumps to a different icon
            // after every rebuild and surprises the user.
            if (SelectedIcon is { } prev)
            {
                SelectedIcon = Icons.FirstOrDefault(i =>
                    string.Equals(i.Filename, prev.Filename, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Icons = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RebuildHashesAsync()
    {
        if (_repositoryService.CurrentRepository is null || IsRebuilding)
        {
            return;
        }

        IsRebuilding = true;
        StatusMessage = "Hashing icons...";
        try
        {
            var progress = new Progress<int>(n =>
            {
                StatusMessage = string.Create(CultureInfo.InvariantCulture, $"Hashing icons... {n}");
            });
            var result = await _iconService.RebuildHashesAsync(progress).ConfigureAwait(true);
            if (result.Success)
            {
                StatusMessage = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Rebuilt _icon_hashes.plist ({result.HashedCount} icons).");
                await LoadAsync().ConfigureAwait(true);
            }
            else
            {
                StatusMessage = result.ErrorMessage ?? "Icon hash rebuild failed.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Icon hash rebuild error: {ex.Message}";
        }
        finally
        {
            IsRebuilding = false;
        }
    }

    private void OnIconHashesChanged(object? sender, EventArgs e)
    {
        _ = LoadAsync();
    }
}
