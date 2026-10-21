// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes which personal data values of a stream to erase.
/// </summary>
/// <remarks>
/// The selected events are the events named by <see cref="Versions"/>, or every event of the stream when it is
/// <c>null</c>. The selection is every commitment recorded in a selected event that matches <see cref="Paths"/>, or
/// every commitment when it is <c>null</c>, and still has its salt.
/// </remarks>
public sealed class ErasureRequest
{
    /// <summary>
    /// Creates a request.
    /// </summary>
    /// <param name="reference">The reference recorded with the erasure, for example a ticket number, without personal data.</param>
    /// <param name="versions">The versions of the events to erase from, or <c>null</c> for every event of the stream.</param>
    /// <param name="paths">The dotted element paths to erase, or <c>null</c> for every recorded path.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="reference"/> is empty, <paramref name="versions"/> or <paramref name="paths"/> is
    /// empty, or a path is empty.
    /// </exception>
    public ErasureRequest(String reference, IReadOnlyCollection<Int64>? versions = null, IReadOnlyCollection<String>? paths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (versions is { Count: 0 })
        {
            throw new ArgumentException("The requested versions must not be empty; pass null to select every event.", nameof(versions));
        }

        if (paths is { Count: 0 })
        {
            throw new ArgumentException("The requested paths must not be empty; pass null to select every recorded path.", nameof(paths));
        }

        if (paths is not null && paths.Any(String.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Every requested path must be a non-empty element path.", nameof(paths));
        }

        Reference = reference;
        Versions = versions;
        Paths = paths;
    }

    /// <summary>
    /// Gets the dotted element paths to erase, or <c>null</c> for every recorded path.
    /// </summary>
    public IReadOnlyCollection<String>? Paths { get; }

    /// <summary>
    /// Gets the reference recorded with the erasure, for example a ticket number. It is stored in the stream, so
    /// it must not contain personal data itself.
    /// </summary>
    public String Reference { get; }

    /// <summary>
    /// Gets the versions of the events to erase from, or <c>null</c> for every event of the stream.
    /// </summary>
    public IReadOnlyCollection<Int64>? Versions { get; }
}
