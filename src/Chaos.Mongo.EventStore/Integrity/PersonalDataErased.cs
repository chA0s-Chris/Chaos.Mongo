// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Documents in the stream that personal data values of earlier events were erased, and why they are missing.
/// </summary>
/// <remarks>
/// As an observational event it changes neither the revision nor the read model, and replay skips it. Its
/// discriminator is reserved and its element names are pinned, because the verifier reads it from the raw documents.
/// Its class map is registered for every aggregate type, so streams containing it still deserialize when protection
/// is disabled.
/// </remarks>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class PersonalDataErased<TAggregate> : ObservationalEvent<TAggregate>
    where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// The reserved discriminator of the event.
    /// </summary>
    public const String Discriminator = "Chaos.Mongo.EventStore.PersonalDataErased";

    /// <summary>
    /// The pinned element name of <see cref="Entries"/>.
    /// </summary>
    public const String EntriesElement = "Entries";

    /// <summary>
    /// The pinned element name of <see cref="Reference"/>.
    /// </summary>
    public const String ReferenceElement = "Reference";

    /// <summary>
    /// Gets or sets the erased paths per event version.
    /// </summary>
    public List<PersonalDataErasureEntry> Entries { get; set; } = [];

    /// <summary>
    /// Gets or sets the reference given with the erasure request.
    /// </summary>
    public String Reference { get; set; } = String.Empty;
}
