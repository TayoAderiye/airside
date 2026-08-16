using System.Text.RegularExpressions;
using Airside.Core.Containers;

namespace Airside.Runtime.Applications;

/// <summary>
/// Finds the images a Dockerfile pulls from.
/// </summary>
/// <remarks>
/// Needed so a build against a private base image gets a credential. Without it
/// the build fails at the daemon's own pull of <c>FROM</c>, and the error names a
/// missing image rather than a missing login — which sends the operator looking
/// for a typo in a tag that is perfectly correct.
/// </remarks>
public static partial class BaseImages
{
    /// <summary>
    /// External base images, in order, ignoring references to earlier stages.
    /// </summary>
    /// <remarks>
    /// A multi-stage Dockerfile names its own stages (<c>FROM x AS builder</c>) and
    /// then refers back to them (<c>FROM builder</c>). Those are not registry
    /// images, and treating one as a repository would send a token to a registry
    /// called "builder".
    /// </remarks>
    public static IReadOnlyList<ImageReference> Parse(string? dockerfile)
    {
        if (string.IsNullOrWhiteSpace(dockerfile))
        {
            return [];
        }

        var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var images = new List<ImageReference>();

        foreach (Match match in FromPattern().Matches(dockerfile))
        {
            var image = match.Groups["image"].Value.Trim();
            var stage = match.Groups["stage"].Value.Trim();

            // A build argument in the image position cannot be resolved without
            // evaluating the build, so it is skipped rather than guessed at.
            if (image.Length > 0
                && !stages.Contains(image)
                && !image.Contains("${", StringComparison.Ordinal)
                && !image.StartsWith('$'))
            {
                try
                {
                    images.Add(ImageReference.Parse(image));
                }
                catch (FormatException)
                {
                    // An unparseable FROM is the build's problem to report, not
                    // this helper's.
                }
            }

            if (stage.Length > 0)
            {
                stages.Add(stage);
            }
        }

        return images;
    }

    /// <summary>
    /// Matches <c>FROM image[:tag] [AS stage]</c>, ignoring platform flags and comments.
    /// </summary>
    [GeneratedRegex(
        @"^\s*FROM\s+(?:--[^\s]+\s+)*(?<image>[^\s]+)(?:\s+AS\s+(?<stage>[^\s]+))?",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FromPattern();
}
