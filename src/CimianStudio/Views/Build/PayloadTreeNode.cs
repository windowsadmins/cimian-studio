namespace CimianStudio.Views.Build;

using System.Collections.ObjectModel;

/// <summary>
/// Hierarchical row for the payload TreeView. We bind the tree via ItemsSource
/// (rather than the imperative RootNodes API) so the DataTemplate's x:Bind can
/// drive per-row icons and labels — RootNodes mode ignores ItemTemplate, which
/// was the reason the tree showed only chevrons.
/// </summary>
public sealed class PayloadTreeNode
{
    public PayloadTreeNode(string fullPath, string name, bool isDirectory)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }

    /// <summary>
    /// Child entries — populated for directory nodes, always empty for files.
    /// Observable so the tree picks up filesystem changes the watcher pushes.
    /// </summary>
    public ObservableCollection<PayloadTreeNode> Children { get; } = [];

    /// <summary>
    /// Segoe Fluent Icons glyph rendered next to the name (E8B7 = Folder,
    /// E8A5 = Document). Drives the per-row icon in the TreeView's ItemTemplate.
    /// </summary>
    public string Glyph => IsDirectory ? "" : "";
}
