namespace CimianStudio.Views;

using System.Text;

/// <summary>
/// Parses unified-diff output (from <c>git diff</c>) into a tree of
/// <see cref="DiffFile"/>s, each holding the four-line file header (the
/// "envelope" git apply requires to attribute the patch to a file) and a
/// list of <see cref="DiffHunk"/>s. Each hunk carries its own <c>@@</c>
/// position header plus its body lines tagged as context / added / removed.
/// </summary>
internal static class UnifiedDiffParser
{
    public static IReadOnlyList<DiffFile> Parse(string diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return [];
        }

        var files = new List<DiffFile>();
        DiffFile? currentFile = null;
        DiffHunk? currentHunk = null;
        var fileHeaderBuilder = new StringBuilder();
        var inFileHeader = false;

        foreach (var rawLine in diff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                // Flush whatever's in progress.
                FlushHunk(ref currentHunk, currentFile);
                FinishFileHeader(currentFile, fileHeaderBuilder, finalize: true);

                currentFile = new DiffFile { DiffLine = line };
                files.Add(currentFile);
                fileHeaderBuilder.Clear();
                fileHeaderBuilder.Append(line).Append('\n');
                inFileHeader = true;

                // Try to extract a/Path and b/Path off the diff --git line.
                var (a, b) = ExtractPaths(line);
                currentFile.OldPath = a;
                currentFile.NewPath = b;
                continue;
            }

            if (currentFile is null)
            {
                continue; // Skip preamble before any "diff --git" line.
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushHunk(ref currentHunk, currentFile);
                FinishFileHeader(currentFile, fileHeaderBuilder, finalize: true);
                inFileHeader = false;

                var (oldStart, newStart) = ParseHunkPositions(line);
                currentHunk = new DiffHunk
                {
                    Header = line,
                    OldStart = oldStart,
                    NewStart = newStart,
                };
                currentFile.Hunks.Add(currentHunk);
                continue;
            }

            if (inFileHeader)
            {
                fileHeaderBuilder.Append(line).Append('\n');
                continue;
            }

            if (currentHunk is null)
            {
                continue;
            }

            // Inside a hunk: classify by leading char.
            if (line.Length == 0)
            {
                currentHunk.Lines.Add(new DiffLine(DiffLineKind.Context, string.Empty));
                continue;
            }

            var kind = line[0] switch
            {
                '+' => DiffLineKind.Added,
                '-' => DiffLineKind.Removed,
                '\\' => DiffLineKind.NoNewline,
                _ => DiffLineKind.Context,
            };
            currentHunk.Lines.Add(new DiffLine(kind, line));
        }

        FlushHunk(ref currentHunk, currentFile);
        FinishFileHeader(currentFile, fileHeaderBuilder, finalize: true);
        return files;
    }

    private static void FlushHunk(ref DiffHunk? hunk, DiffFile? file)
    {
        // Hunks are appended into file.Hunks at @@-time, so nothing to do here
        // beyond clearing the ref so the next iteration knows we're between
        // hunks. Kept as a helper for symmetry with FinishFileHeader.
        _ = file;
        hunk = null;
    }

    private static void FinishFileHeader(DiffFile? file, StringBuilder builder, bool finalize)
    {
        if (file is null) return;
        if (!finalize) return;
        if (string.IsNullOrEmpty(file.FileHeader))
        {
            file.FileHeader = builder.ToString();
        }
    }

    private static (string A, string B) ExtractPaths(string diffLine)
    {
        // diffLine: "diff --git a/some/path b/some/path"
        const string prefix = "diff --git ";
        if (!diffLine.StartsWith(prefix, StringComparison.Ordinal))
        {
            return (string.Empty, string.Empty);
        }
        var rest = diffLine[prefix.Length..];
        // Split on " b/" so paths containing spaces in the "a/" half still parse.
        var idx = rest.IndexOf(" b/", StringComparison.Ordinal);
        if (idx < 0) return (rest, string.Empty);
        var a = rest[..idx];
        var b = rest[(idx + 1)..];
        if (a.StartsWith("a/", StringComparison.Ordinal)) a = a[2..];
        if (b.StartsWith("b/", StringComparison.Ordinal)) b = b[2..];
        return (a, b);
    }

    private static (int OldStart, int NewStart) ParseHunkPositions(string header)
    {
        // @@ -OldStart,OldCount +NewStart,NewCount @@ optional section heading
        // Counts default to 1 when omitted.
        var oldStart = 0;
        var newStart = 0;
        var inMinus = false;
        var inPlus = false;
        var token = new StringBuilder();
        foreach (var ch in header)
        {
            if (ch == '-')
            {
                inMinus = true;
                inPlus = false;
                token.Clear();
            }
            else if (ch == '+')
            {
                inPlus = true;
                inMinus = false;
                token.Clear();
            }
            else if (char.IsDigit(ch))
            {
                token.Append(ch);
            }
            else if (token.Length > 0)
            {
                if (inMinus && oldStart == 0)
                {
                    _ = int.TryParse(token.ToString(), out oldStart);
                    inMinus = false;
                }
                else if (inPlus && newStart == 0)
                {
                    _ = int.TryParse(token.ToString(), out newStart);
                    inPlus = false;
                }
                token.Clear();
            }
        }
        return (oldStart, newStart);
    }
}

internal sealed class DiffFile
{
    /// <summary>The full <c>diff --git ...</c> line.</summary>
    public string DiffLine { get; set; } = string.Empty;

    /// <summary>
    /// The four-line envelope git apply needs: <c>diff --git</c>, <c>index</c>,
    /// <c>---</c>, <c>+++</c>. Prepended to a hunk when building a single-hunk
    /// patch for <c>git apply</c>.
    /// </summary>
    public string FileHeader { get; set; } = string.Empty;

    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;

    public List<DiffHunk> Hunks { get; } = [];

    /// <summary>Effective path to show in the file-card header.</summary>
    public string DisplayPath => string.IsNullOrEmpty(NewPath) ? OldPath : NewPath;
}

internal sealed class DiffHunk
{
    public string Header { get; set; } = string.Empty;
    public int OldStart { get; set; }
    public int NewStart { get; set; }
    public List<DiffLine> Lines { get; } = [];

    public int AddedCount => Lines.Count(l => l.Kind == DiffLineKind.Added);
    public int RemovedCount => Lines.Count(l => l.Kind == DiffLineKind.Removed);

    /// <summary>
    /// Reconstructs a minimal patch text containing this hunk under the
    /// supplied file header. Ready to pipe to <c>git apply</c>. LF endings
    /// throughout — git apply on Windows misparses CRLF inside hunks.
    /// </summary>
    public string ToPatch(DiffFile file)
    {
        var sb = new StringBuilder();
        sb.Append(file.FileHeader);
        if (!file.FileHeader.EndsWith('\n')) sb.Append('\n');
        sb.Append(Header).Append('\n');
        foreach (var line in Lines)
        {
            sb.Append(line.Raw).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds a partial patch that only includes the lines whose
    /// <see cref="DiffLine.IsSelected"/> flag is true. Unselected <c>+</c>
    /// lines drop out (the line stays in the working tree but isn't part of
    /// the index change); unselected <c>-</c> lines are converted to context
    /// (the line stays in HEAD AND in the index). The <c>@@</c> header is
    /// recounted so <c>git apply</c> accepts the result.
    /// </summary>
    /// <returns>
    /// A patch string, or null when no line is selected and / or after
    /// filtering there are no actual changes left in the hunk.
    /// </returns>
    public string? ToPartialPatch(DiffFile file)
    {
        // Walk lines and rewrite them per the selection rule above.
        var filtered = new List<string>();
        var oldCount = 0;
        var newCount = 0;
        var hasChange = false;

        foreach (var line in Lines)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Context:
                    filtered.Add(line.Raw);
                    oldCount++;
                    newCount++;
                    break;
                case DiffLineKind.Added:
                    if (line.IsSelected)
                    {
                        filtered.Add(line.Raw);
                        newCount++;
                        hasChange = true;
                    }
                    // else: drop entirely
                    break;
                case DiffLineKind.Removed:
                    if (line.IsSelected)
                    {
                        filtered.Add(line.Raw);
                        oldCount++;
                        hasChange = true;
                    }
                    else
                    {
                        // Convert "-foo" to " foo" so the line stays in both
                        // sides of the resulting patch.
                        filtered.Add(' ' + line.Raw[1..]);
                        oldCount++;
                        newCount++;
                    }
                    break;
                case DiffLineKind.NoNewline:
                    filtered.Add(line.Raw);
                    break;
            }
        }

        if (!hasChange)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append(file.FileHeader);
        if (!file.FileHeader.EndsWith('\n')) sb.Append('\n');
        sb.Append("@@ -").Append(OldStart).Append(',').Append(oldCount)
          .Append(" +").Append(NewStart).Append(',').Append(newCount)
          .Append(" @@").Append('\n');
        foreach (var line in filtered) sb.Append(line).Append('\n');
        return sb.ToString();
    }
}

internal enum DiffLineKind
{
    Context,
    Added,
    Removed,
    NoNewline,
}

/// <summary>
/// Mutable so the diff renderer can flip <see cref="IsSelected"/> on user
/// click and the patch builder can read selection state. Reference type, not
/// struct, because the renderer holds direct references from row click
/// handlers.
/// </summary>
internal sealed class DiffLine
{
    public DiffLineKind Kind { get; }
    public string Raw { get; }
    public bool IsSelected { get; set; }

    public DiffLine(DiffLineKind kind, string raw)
    {
        Kind = kind;
        Raw = raw;
    }

    /// <summary>The line content without the leading +/-/space.</summary>
    public string Text => Raw.Length > 0 ? Raw[1..] : string.Empty;
}
