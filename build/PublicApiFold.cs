// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

/// <summary>
/// Folds a project's <c>PublicAPI.Unshipped.txt</c> into its <c>PublicAPI.Shipped.txt</c> when a release ships its API.
/// Has no dependency on Nuke or the file system, so it can be linked into a test project.
/// </summary>
internal static class PublicApiFold
{
    /// <summary>
    /// The first line of every API file. The analyzer reads entries after it as nullable-annotated.
    /// </summary>
    public const String Header = "#nullable enable";

    private const String RemovedPrefix = "*REMOVED*";

    /// <summary>
    /// Applies the unshipped declarations to the shipped ones: every entry is added to the shipped file, and every
    /// <c>*REMOVED*&lt;entry&gt;</c> deletes <c>&lt;entry&gt;</c> from it. Shipped entries are written in ordinal order,
    /// so the result does not depend on the order in which the API was declared.
    /// </summary>
    /// <param name="projectName">The project the files belong to, named in error messages.</param>
    /// <param name="shipped">The content of <c>PublicAPI.Shipped.txt</c>.</param>
    /// <param name="unshipped">The content of <c>PublicAPI.Unshipped.txt</c>.</param>
    /// <returns>The new content of both files; the unshipped file keeps only the header.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a file does not start with <see cref="Header"/>, when a removed entry is not in the shipped file,
    /// or when an added entry is already shipped. Nothing is folded then, so the caller can leave every file untouched.
    /// </exception>
    public static PublicApiFoldResult Fold(String projectName, String shipped, String unshipped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(unshipped);

        var shippedEntries = ReadEntries(projectName, "PublicAPI.Shipped.txt", shipped);
        var unshippedEntries = ReadEntries(projectName, "PublicAPI.Unshipped.txt", unshipped);

        var result = new HashSet<String>(shippedEntries, StringComparer.Ordinal);
        // The analyzer accepts an entry listed twice, and the unshipped files have held repeated removals, so each
        // distinct entry counts once.
        var removed = unshippedEntries.Where(entry => entry.StartsWith(RemovedPrefix, StringComparison.Ordinal))
                                      .Select(entry => entry[RemovedPrefix.Length..])
                                      .Distinct(StringComparer.Ordinal)
                                      .ToList();
        var added = unshippedEntries.Where(entry => !entry.StartsWith(RemovedPrefix, StringComparison.Ordinal))
                                    .Distinct(StringComparer.Ordinal)
                                    .ToList();

        var missing = removed.Where(entry => !result.Remove(entry)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(Describe(projectName, "removes entries that are not shipped", missing));

        var duplicates = added.Where(entry => !result.Add(entry)).ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException(Describe(projectName, "adds entries that are already shipped", duplicates));

        var lines = result.Order(StringComparer.Ordinal).Prepend(Header);
        return new PublicApiFoldResult($"{String.Join('\n', lines)}\n", $"{Header}\n");
    }

    private static String Describe(String projectName, String problem, IEnumerable<String> entries)
        => $"{projectName}: PublicAPI.Unshipped.txt {problem}:{String.Concat(entries.Select(entry => $"\n  {entry}"))}";

    private static IReadOnlyList<String> ReadEntries(String projectName, String fileName, String content)
    {
        var lines = content.Split('\n').Select(line => line.TrimEnd('\r').Trim()).ToList();

        if (lines[0] != Header)
            throw new InvalidOperationException($"{projectName}: {fileName} does not start with \"{Header}\".");

        return lines.Skip(1).Where(line => line.Length > 0).ToList();
    }
}

/// <summary>
/// The new content of a project's API files after <see cref="PublicApiFold.Fold"/>.
/// </summary>
/// <param name="Shipped">The new content of <c>PublicAPI.Shipped.txt</c>.</param>
/// <param name="Unshipped">The new content of <c>PublicAPI.Unshipped.txt</c>.</param>
internal sealed record PublicApiFoldResult(String Shipped, String Unshipped);
