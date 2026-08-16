using Airside.Core.Common;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace Airside.Runtime.Applications;

public sealed record GitCheckout(string CommitSha, string? CommitMessage, string Branch);

/// <summary>
/// Clones a repository for a build.
/// </summary>
/// <remarks>
/// <para>
/// In-process through libgit2 rather than by running <c>git</c>. Shelling out
/// would break the rule that Airside executes no host commands, and would put a
/// git binary in the API image purely so a string could be handed to it.
/// </para>
/// <para>
/// The URL is validated before libgit2 sees it. libgit2 supports transports —
/// <c>file://</c>, and <c>ssh://</c> with an agent — that would let a repository
/// URL read the control plane's own filesystem, so the scheme is allowlisted
/// rather than filtered.
/// </para>
/// </remarks>
public sealed class GitSource(ILogger<GitSource> logger)
{
    private static readonly string[] AllowedSchemes = ["https", "http"];

    public static Result<string> ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Rejected("A repository URL is required.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Rejected("The repository URL is not a valid absolute URL.");
        }

        if (!AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            // file:// would clone from the control plane's own disk; ssh:// would
            // use whatever key the process can reach. Neither is something a
            // repository URL should be able to ask for.
            return Rejected(
                $"Only https and http repository URLs are supported, not '{uri.Scheme}'.");
        }

        return url;
    }

    public async Task<GitCheckout> CloneAsync(
        string repositoryUrl,
        string? branch,
        string destination,
        CancellationToken ct)
    {
        var validated = ValidateUrl(repositoryUrl);

        if (validated.IsFailure)
        {
            throw new InvalidOperationException(validated.Failure!.Message);
        }

        var options = new CloneOptions { RecurseSubmodules = false };

        if (!string.IsNullOrWhiteSpace(branch))
        {
            options.BranchName = branch;
        }

        // libgit2 is synchronous and CPU/IO heavy; keeping it off the job
        // dispatcher's thread stops one slow clone stalling the whole queue.
        return await Task.Run(
            () =>
            {
                logger.LogInformation("Cloning {Repository} into the build workspace", repositoryUrl);

                Repository.Clone(repositoryUrl, destination, options);

                using var repository = new Repository(destination);
                var tip = repository.Head.Tip;

                return new GitCheckout(
                    tip.Sha,
                    tip.MessageShort,
                    repository.Head.FriendlyName);
            },
            ct).ConfigureAwait(false);
    }

    private static Error Rejected(string message) => new(
        ErrorCodes.ValidationFailed,
        message,
        new Dictionary<string, object?>(System.StringComparer.Ordinal) { ["field"] = "repositoryUrl" });
}
