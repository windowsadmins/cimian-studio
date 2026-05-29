namespace CimianStudio.Views;

using System.ComponentModel;
using System.Globalization;
using CimianStudio.Core.Models.Packages;
using CimianStudio.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

public sealed partial class DevelopersPage : Page
{
    public DevelopersViewModel ViewModel { get; }

    public DevelopersPage(DevelopersViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync().ConfigureAwait(true);
        RefreshHeader();
        RefreshDetail();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DevelopersViewModel.Developers):
                RefreshHeader();
                break;
            case nameof(DevelopersViewModel.SelectedDeveloper):
                RefreshDetail();
                break;
        }
    }

    private void RefreshHeader()
    {
        CountText.Text = string.Create(CultureInfo.InvariantCulture, $"({ViewModel.Developers.Count})");
    }

    private void RefreshDetail()
    {
        var group = ViewModel.SelectedDeveloper;
        if (group is null)
        {
            PackagesList.ItemsSource = null;
            DetailTitleText.Text = string.Empty;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        DetailTitleText.Text = string.Create(CultureInfo.InvariantCulture, $"{group.Name} · {group.Count}");
        PackagesList.ItemsSource = group.Packages;
    }

    private void OnPackageClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Package pkg)
        {
            App.MainWindowInstance?.NavigateToPackage(pkg);
        }
    }
}
