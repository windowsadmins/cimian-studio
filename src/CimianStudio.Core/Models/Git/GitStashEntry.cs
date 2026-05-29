namespace CimianStudio.Core.Models.Git;

/// <summary>
/// One entry from <c>git stash list</c>. <see cref="Reference"/> is the
/// <c>stash@{N}</c> token git uses to identify the stash in subsequent
/// pop / apply / drop operations.
/// </summary>
/// <param name="Reference">Stash reference such as <c>stash@{0}</c>.</param>
/// <param name="Branch">Branch the stash was created on (parsed from the subject line).</param>
/// <param name="Subject">Stash message (or the auto-generated "WIP on …" if no message was given).</param>
/// <param name="When">Commit time of the stash commit.</param>
public sealed record GitStashEntry(
    string Reference,
    string Branch,
    string Subject,
    DateTimeOffset When);
