namespace CimianStudio.Views.Build;

using Microsoft.UI.Xaml.Controls;

/// <summary>
/// Backing model attached to a <see cref="TreeViewNode.Content"/> entry for each
/// row in the payload tree. The <see cref="TreeViewNode"/> itself owns the
/// expanded/collapsed state and child-node list; this record just gives us a
/// stable path → display-name mapping when the user picks a row.
/// </summary>
public sealed record PayloadTreeNode(string FullPath, string Name, bool IsDirectory);
