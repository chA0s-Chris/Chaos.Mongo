// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Extensions for <see cref="IMongoIndexManager{TDocument}"/>.
/// </summary>
public static class MongoIndexManagerExtensions
{
    /// <summary>
    /// How long the probe's best-effort cleanup (<see cref="DropProbeBestEffortAsync"/>) may run on its
    /// own independent context before giving up, so a stuck cleanup can never hang indefinitely.
    /// </summary>
    private static readonly TimeSpan ProbeCleanupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates an index, or updates it if the index already exists with different options or specifications.
    /// If the index exists with conflicting options or key specifications, it will be dropped and recreated.
    /// The existing index is located by its key specification and options, not by the name the server reports
    /// in the conflict, so renaming an index that keeps the same key specification is handled correctly. A
    /// concurrent caller resolving the same conflict is tolerated.
    /// </summary>
    /// <typeparam name="TDocument">The type of the document.</typeparam>
    /// <param name="indexManager">The index manager.</param>
    /// <param name="model">The create index model.</param>
    /// <param name="options">The options for creating the index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The name of the index that was created or updated.</returns>
    /// <exception cref="MongoCommandException">
    /// Thrown when an error occurs that is not related to index conflicts, when the
    /// conflicting index cannot be identified, or when resolving the conflict requires
    /// resolving the model's effective <c>Collation</c> (see <see cref="ProbeEffectiveCollationAsync{TDocument}"/>)
    /// and the target collection is already at MongoDB's 64-index limit: that probe needs one spare
    /// index slot, so at capacity the original index conflict is thrown instead of silently succeeding.
    /// </exception>
    public static async Task<String?> CreateOneOrUpdateAsync<TDocument>(this IMongoIndexManager<TDocument> indexManager,
                                                                        CreateIndexModel<TDocument> model,
                                                                        CreateOneIndexOptions? options = null,
                                                                        CancellationToken cancellationToken = default)
    {
        try
        {
            return await indexManager.CreateOneAsync(model, options, cancellationToken);
        }
        catch (MongoCommandException e) when (IsIndexConflict(e))
        {
            var requestedName = GetRequestedIndexName(e);
            if (String.IsNullOrEmpty(requestedName))
            {
                throw;
            }

            return await ResolveConflictAsync(
                indexManager,
                model,
                requestedName,
                e,
                ct => indexManager.ListAsync(ct),
                (name, ct) => indexManager.DropOneIfExistsAsync(name, ct),
                () => indexManager.CreateOneAsync(model, options, cancellationToken),
                (probe, ct) => indexManager.CreateOneAsync(probe, null, ct),
                cancellationToken);
        }
    }

    /// <summary>
    /// Creates an index within a session, or updates it if the index already exists with different options or specifications.
    /// If the index exists with conflicting options or key specifications, it will be dropped and recreated.
    /// The existing index is located by its key specification and options, not by the name the server reports
    /// in the conflict, so renaming an index that keeps the same key specification is handled correctly. A
    /// concurrent caller resolving the same conflict is tolerated.
    /// </summary>
    /// <typeparam name="TDocument">The type of the document.</typeparam>
    /// <param name="indexManager">The index manager.</param>
    /// <param name="session">The client session.</param>
    /// <param name="model">The create index model.</param>
    /// <param name="options">The options for creating the index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The name of the index that was created or updated.</returns>
    /// <exception cref="MongoCommandException">
    /// Thrown when an error occurs that is not related to index conflicts, when the
    /// conflicting index cannot be identified, or when resolving the conflict requires
    /// resolving the model's effective <c>Collation</c> (see <see cref="ProbeEffectiveCollationAsync{TDocument}"/>)
    /// and the target collection is already at MongoDB's 64-index limit: that probe needs one spare
    /// index slot, so at capacity the original index conflict is thrown instead of silently succeeding.
    /// </exception>
    public static async Task<String?> CreateOneOrUpdateAsync<TDocument>(this IMongoIndexManager<TDocument> indexManager,
                                                                        IClientSessionHandle session,
                                                                        CreateIndexModel<TDocument> model,
                                                                        CreateOneIndexOptions? options = null,
                                                                        CancellationToken cancellationToken = default)
    {
        try
        {
            return await indexManager.CreateOneAsync(session, model, options, cancellationToken);
        }
        catch (MongoCommandException e) when (IsIndexConflict(e))
        {
            var requestedName = GetRequestedIndexName(e);
            if (String.IsNullOrEmpty(requestedName))
            {
                throw;
            }

            return await ResolveConflictAsync(
                indexManager,
                model,
                requestedName,
                e,
                ct => indexManager.ListAsync(session, ct),
                (name, ct) => indexManager.DropOneIfExistsAsync(session, name, ct),
                () => indexManager.CreateOneAsync(session, model, options, cancellationToken),
                (probe, ct) => indexManager.CreateOneAsync(session, probe, null, ct),
                cancellationToken);
        }
    }

    /// <summary>
    /// Drops an index if it exists.
    /// Missing index or collection errors are ignored.
    /// </summary>
    /// <typeparam name="TDocument">The type of the document.</typeparam>
    /// <param name="indexManager">The index manager.</param>
    /// <param name="name">The name of the index to drop.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="MongoCommandException">
    /// Thrown when an error occurs that is not related to a missing index or
    /// collection.
    /// </exception>
    public static async Task DropOneIfExistsAsync<TDocument>(this IMongoIndexManager<TDocument> indexManager,
                                                             String name,
                                                             CancellationToken cancellationToken = default)
        => await ExecuteDropOneAsync(() => indexManager.DropOneAsync(name, cancellationToken));

    /// <summary>
    /// Drops an index if it exists.
    /// Missing index or collection errors are ignored.
    /// </summary>
    /// <typeparam name="TDocument">The type of the document.</typeparam>
    /// <param name="indexManager">The index manager.</param>
    /// <param name="name">The name of the index to drop.</param>
    /// <param name="options">The options for dropping the index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="MongoCommandException">
    /// Thrown when an error occurs that is not related to a missing index or
    /// collection.
    /// </exception>
    public static async Task DropOneIfExistsAsync<TDocument>(this IMongoIndexManager<TDocument> indexManager,
                                                             String name,
                                                             DropIndexOptions options,
                                                             CancellationToken cancellationToken = default)
        => await ExecuteDropOneAsync(() => indexManager.DropOneAsync(name, options, cancellationToken));

    /// <summary>
    /// Drops an index within a session if it exists.
    /// Missing index or collection errors are ignored.
    /// </summary>
    /// <typeparam name="TDocument">The type of the document.</typeparam>
    /// <param name="indexManager">The index manager.</param>
    /// <param name="session">The client session.</param>
    /// <param name="name">The name of the index to drop.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="MongoCommandException">
    /// Thrown when an error occurs that is not related to a missing index or
    /// collection.
    /// </exception>
    public static async Task DropOneIfExistsAsync<TDocument>(this IMongoIndexManager<TDocument> indexManager,
                                                             IClientSessionHandle session,
                                                             String name,
                                                             CancellationToken cancellationToken = default)
        => await ExecuteDropOneAsync(() => indexManager.DropOneAsync(session, name, cancellationToken));

    /// <summary>
    /// Drops an index within a session if it exists.
    /// Missing index or collection errors are ignored.
    /// </summary>
    /// <typeparam name="TDocument">The type of the document.</typeparam>
    /// <param name="indexManager">The index manager.</param>
    /// <param name="session">The client session.</param>
    /// <param name="name">The name of the index to drop.</param>
    /// <param name="options">The options for dropping the index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="MongoCommandException">
    /// Thrown when an error occurs that is not related to a missing index or
    /// collection.
    /// </exception>
    public static async Task DropOneIfExistsAsync<TDocument>(this IMongoIndexManager<TDocument> indexManager,
                                                             IClientSessionHandle session,
                                                             String name,
                                                             DropIndexOptions options,
                                                             CancellationToken cancellationToken = default)
        => await ExecuteDropOneAsync(() => indexManager.DropOneAsync(session, name, options, cancellationToken));

    /// <summary>
    /// Builds the desired <see cref="IndexDefinition"/> for <paramref name="model"/>: its rendered keys, its
    /// normalized identity/effective options, and its effective <c>Collation</c>, resolved via
    /// <see cref="ProbeEffectiveCollationAsync{TDocument}"/> when (and only when) it cannot be trusted literally
    /// (see <see cref="RequiresCollationProbe"/>). If the probe itself cannot be created because the target
    /// collection is already at MongoDB's 64-index limit, <paramref name="originalConflict"/> is thrown instead
    /// of the probe's own <c>CannotCreateIndex</c> failure: resolving the collation needs one spare index slot in
    /// the target collection, and a caller at capacity should see the original index conflict, not a confusing
    /// error about an internal throwaway index it never asked for.
    /// </summary>
    private static async Task<IndexDefinition> BuildDesiredDefinitionAsync<TDocument>(IMongoIndexManager<TDocument> indexManager,
                                                                                      CreateIndexModel<TDocument> model,
                                                                                      IReadOnlyList<ExistingIndex> indexes,
                                                                                      Func<CreateIndexModel<TDocument>, CancellationToken, Task<String>> createProbe,
                                                                                      Func<CancellationToken, Task<IAsyncCursor<BsonDocument>>> listIndexes,
                                                                                      Func<String, CancellationToken, Task> dropOneIfExists,
                                                                                      CancellationToken cancellationToken,
                                                                                      MongoCommandException originalConflict)
    {
        var renderArgs = new RenderArgs<TDocument>(indexManager.DocumentSerializer, indexManager.Settings.SerializerRegistry);
        var options = model.Options;
        var renderedKeys = model.Keys.Render(renderArgs);
        var textFields = ExtractTextFieldNames(renderedKeys, options?.Weights);
        var keys = NormalizeRequestedKeys(renderedKeys, textFields);

        BsonDocument? collation = null;
        if (RequiresCollationProbe(options?.Collation, keys, indexes))
        {
            try
            {
                collation = await ProbeEffectiveCollationAsync<TDocument>(options?.Collation, createProbe, listIndexes, dropOneIfExists, cancellationToken);
            }
            catch (MongoCommandException e) when (e.CodeName == "CannotCreateIndex")
            {
                throw originalConflict;
            }
        }

        var isGeo2D = Is2DIndex(keys);

        return new IndexDefinition(
            keys,
            options?.Unique ?? false,
            options?.Sparse ?? false,
            options?.PartialFilterExpression?.Render(renderArgs),
            collation,
            options?.ExpireAfter,
            NormalizeDesiredWeights(textFields, options?.Weights),
            NormalizeDesiredDefaultLanguage(textFields, options?.DefaultLanguage),
            NormalizeDesiredLanguageOverride(textFields, options?.LanguageOverride),
            NormalizeDesiredTextIndexVersion(textFields, options?.TextIndexVersion),
            options?.WildcardProjection?.Render(renderArgs),
            isGeo2D ? options?.Bits ?? 26 : null,
            isGeo2D ? options?.Min ?? -180.0 : null,
            isGeo2D ? options?.Max ?? 180.0 : null,
            options?.Hidden ?? false,
            options?.StorageEngine);
    }

    /// <summary>
    /// Drops the collation probe's throwaway index on a bounded context that is independent of the caller's own
    /// (possibly already canceled) token, so the cleanup itself is never skipped or immediately canceled. Any
    /// failure here, including this context's own timeout, is deliberately swallowed: cleanup is best-effort and
    /// must never replace or mask the primary create/list result or exception that is already propagating out of
    /// <see cref="ProbeEffectiveCollationAsync{TDocument}"/>'s try block.
    /// </summary>
    private static async Task DropProbeBestEffortAsync(String probeName, Func<String, CancellationToken, Task> dropOneIfExists)
    {
        using var cleanupCts = new CancellationTokenSource(ProbeCleanupTimeout);
        try
        {
            await dropOneIfExists(probeName, cleanupCts.Token);
        }
        catch (Exception)
        {
            // Best-effort: see the method summary.
        }
    }

    /// <summary>
    /// Identity equality plus every other option the server persists and could cause <c>CreateOneOrUpdateAsync</c>
    /// to wrongly report a same-name request as already satisfied: <c>ExpireAfter</c>, <c>Hidden</c>,
    /// <c>StorageEngine</c>, and (for a text index) <c>Weights</c>, <c>DefaultLanguage</c>, <c>LanguageOverride</c>,
    /// <c>TextIndexVersion</c>, (for a wildcard index) <c>WildcardProjection</c>, and (for a 2d index) <c>Bits</c>,
    /// <c>Min</c>, <c>Max</c>. An index that is effectively equal to the desired model is already the exact end
    /// state the caller is requesting. None of these participate in identity: the server does not treat any of
    /// them as distinguishing two same-key indexes filed under different names (only one text or overlapping
    /// wildcard/2d index can coexist on the same keys in practice), so an existing index's current value for any
    /// of them need not equal the desired one for it to be the conflicting index — but a same-name index whose
    /// value differs must still be dropped and recreated, not falsely treated as already matching.
    /// </summary>
    private static Boolean EffectiveEquals(IndexDefinition a, IndexDefinition b)
        => IdentityEquals(a, b) &&
           a.ExpireAfter == b.ExpireAfter &&
           a.Hidden == b.Hidden &&
           NullableDocumentEquals(a.StorageEngine, b.StorageEngine) &&
           NullableDocumentEquals(a.Weights, b.Weights) &&
           a.DefaultLanguage == b.DefaultLanguage &&
           a.LanguageOverride == b.LanguageOverride &&
           a.TextIndexVersion == b.TextIndexVersion &&
           NullableDocumentEquals(a.WildcardProjection, b.WildcardProjection) &&
           a.Bits == b.Bits &&
           a.Min.Equals(b.Min) &&
           a.Max.Equals(b.Max);

    private static async Task ExecuteDropOneAsync(Func<Task> dropOne)
    {
        try
        {
            await dropOne.Invoke();
        }
        catch (MongoCommandException e) when (e.CodeName is "IndexNotFound" or "NamespaceNotFound")
        {
            // ignore missing index
        }
    }

    /// <summary>
    /// Extracts the complete effective set of text-indexed field names for a requested model, shared by
    /// <see cref="NormalizeRequestedKeys"/> (key-shape normalization) and <see cref="NormalizeDesiredWeights"/>
    /// (weights normalization), so both agree on exactly which fields are the text fields. This is the union of
    /// the fields rendered as <c>{field: "text"}</c> in the key specification and the fields named in
    /// <paramref name="weights"/>: MongoDB accepts (and actually indexes) a field declared only through
    /// <see cref="CreateIndexOptions.Weights"/>, even when that field is absent from the caller-facing key
    /// document, so a field introduced this way must not be silently dropped from either comparison. When the key
    /// specification has no explicit <c>"text"</c> field at all, this is not a text index, and
    /// <paramref name="weights"/> (if set at all, which would itself be invalid without a text field) is ignored.
    /// </summary>
    private static List<String> ExtractTextFieldNames(BsonDocument renderedKeys, BsonDocument? weights)
    {
        var textFields = renderedKeys.Elements.Where(element => element.Value.IsString && element.Value.AsString == "text")
                                      .Select(element => element.Name)
                                      .ToList();

        if (textFields.Count == 0 || weights is null)
        {
            return textFields;
        }

        foreach (var name in weights.Names)
        {
            if (!textFields.Contains(name))
            {
                textFields.Add(name);
            }
        }

        return textFields;
    }

    private static String? FindFullySatisfyingIndexName(IReadOnlyList<ExistingIndex> indexes, String requestedName, IndexDefinition desired)
        => indexes.FirstOrDefault(index => index.Name == requestedName && EffectiveEquals(index.Definition, desired)).Name;

    private static String? GetRequestedIndexName(MongoCommandException exception)
        => exception.Command["indexes"][0]["name"].AsString;

    /// <summary>
    /// Compares the rendered key specification together with the options that MongoDB treats as distinguishing
    /// two indexes that otherwise share the same keys (<c>Unique</c>, <c>Sparse</c>, <c>PartialFilterExpression</c>,
    /// <c>Collation</c>). Two indexes with equal identity definitions cannot coexist under different names.
    /// <c>Collation</c> is compared as a fully resolved document on both sides (see
    /// <see cref="BuildDesiredDefinitionAsync{TDocument}"/>
    /// and <see cref="ParseExisting"/>), not by only the fields the caller happened to set, so two indexes that
    /// resolve to the same effective collation are correctly identified as the same identity even when one side
    /// specified it explicitly and the other omitted it and inherited it from the collection.
    /// </summary>
    private static Boolean IdentityEquals(IndexDefinition a, IndexDefinition b)
        => a.Keys.Equals(b.Keys) &&
           a.Unique == b.Unique &&
           a.Sparse == b.Sparse &&
           NullableDocumentEquals(a.PartialFilter, b.PartialFilter) &&
           NullableDocumentEquals(a.Collation, b.Collation);

    /// <summary>
    /// Determines whether a (normalized) key specification describes a legacy 2d geospatial index, i.e. it
    /// contains a field rendered as <c>{field: "2d"}</c>, so <c>Bits</c>/<c>Min</c>/<c>Max</c> (see
    /// <see cref="EffectiveEquals"/>) are applicable to it. These options are conflict-relevant only for a 2d
    /// index: a non-geo index never has them, and a <c>2dsphere</c> index uses an unrelated option
    /// (<c>SphereIndexVersion</c>), out of scope for this check.
    /// </summary>
    private static Boolean Is2DIndex(BsonDocument keys)
        => keys.Elements.Any(element => element.Value.IsString && element.Value.AsString == "2d");

    private static Boolean IsIndexConflict(MongoCommandException exception)
        => exception.CodeName is "IndexOptionsConflict" or "IndexKeySpecsConflict";

    private static async Task<List<ExistingIndex>> ListExistingIndexesAsync(Func<CancellationToken, Task<IAsyncCursor<BsonDocument>>> listIndexes,
                                                                            CancellationToken cancellationToken)
    {
        using var cursor = await listIndexes(cancellationToken);
        var documents = await cursor.ToListAsync();
        return documents.ConvertAll(ParseExisting);
    }

    /// <summary>
    /// Builds the desired side's effective <c>default_language</c> for a text index: the caller's explicit
    /// <see cref="CreateIndexOptions.DefaultLanguage"/>, or the server's documented default of <c>"english"</c>.
    /// Returns <see langword="null"/> for a non-text index (no <paramref name="textFields"/>).
    /// </summary>
    private static String? NormalizeDesiredDefaultLanguage(IReadOnlyList<String> textFields, String? explicitDefaultLanguage)
        => textFields.Count == 0 ? null : explicitDefaultLanguage ?? "english";

    /// <summary>
    /// Builds the desired side's effective <c>language_override</c> for a text index: the caller's explicit
    /// <see cref="CreateIndexOptions.LanguageOverride"/>, or the server's documented default of <c>"language"</c>.
    /// Returns <see langword="null"/> for a non-text index (no <paramref name="textFields"/>).
    /// </summary>
    private static String? NormalizeDesiredLanguageOverride(IReadOnlyList<String> textFields, String? explicitLanguageOverride)
        => textFields.Count == 0 ? null : explicitLanguageOverride ?? "language";

    /// <summary>
    /// Builds the desired side's effective <c>textIndexVersion</c> for a text index: the caller's explicit
    /// <see cref="CreateIndexOptions.TextIndexVersion"/>, or the server's documented default of <c>3</c>. Returns
    /// <see langword="null"/> for a non-text index (no <paramref name="textFields"/>).
    /// </summary>
    private static Int32? NormalizeDesiredTextIndexVersion(IReadOnlyList<String> textFields, Int32? explicitTextIndexVersion)
        => textFields.Count == 0 ? null : explicitTextIndexVersion ?? 3;

    /// <summary>
    /// Builds the desired side's effective <c>weights</c> document for a text index: every indexed field gets its
    /// explicitly requested weight, or the server's documented default of <c>1</c> for any field the caller did
    /// not list in <see cref="CreateIndexOptions.Weights"/>. Unlike <c>Collation</c>, this default is fixed and
    /// never varies by locale or any other server-side state, so it can be reproduced locally without asking the
    /// server. Returns <see langword="null"/> for a non-text index (no <paramref name="textFields"/>).
    /// </summary>
    private static BsonDocument? NormalizeDesiredWeights(IReadOnlyList<String> textFields, BsonDocument? explicitWeights)
    {
        if (textFields.Count == 0)
        {
            return null;
        }

        var normalized = new BsonDocument();
        foreach (var field in textFields.OrderBy(name => name, StringComparer.Ordinal))
        {
            var weight = explicitWeights is not null && explicitWeights.TryGetValue(field, out var value) ? value : new BsonInt32(1);
            normalized.Add(field, weight);
        }

        return normalized;
    }

    /// <summary>
    /// Drops the server-reported <c>version</c> field (the ICU collator version), which is not a caller-specified
    /// option and is constant across every index on the same server, so it never actually distinguishes two
    /// indexes and would otherwise never match a desired collation resolved via
    /// <see cref="ProbeEffectiveCollationAsync{TDocument}"/>.
    /// </summary>
    private static BsonDocument NormalizeExistingCollation(BsonDocument collation)
        => new(collation.Elements.Where(element => element.Name != "version"));

    /// <summary>
    /// Reconstructs the requested-side key shape (see <see cref="NormalizeRequestedKeys"/>) from a
    /// <c>listIndexes</c> document: the actual text-indexed field names live in <c>weights</c>, not in
    /// <c>key</c>, which only ever reports the fixed, consecutive <c>_fts</c>/<c>_ftsx</c> pair for them. The
    /// <c>$text</c> marker replaces that pair in place, at the position where it appears among the compound key's
    /// other fields, instead of being appended at the end: a prefix field (e.g. <c>{prefix:1, content:"text"}</c>)
    /// and a suffix field (e.g. <c>{content:"text", prefix:1}</c>) are different key specifications that must not
    /// compare equal, matching the symmetric handling in <see cref="NormalizeRequestedKeys"/>.
    /// </summary>
    private static BsonDocument NormalizeExistingKeys(BsonDocument document)
    {
        var key = document["key"].AsBsonDocument;
        if (!document.Contains("weights"))
        {
            return key;
        }

        var normalized = new BsonDocument();
        var textGroupAdded = false;
        foreach (var element in key.Elements)
        {
            if (element.Name is "_fts" or "_ftsx")
            {
                if (!textGroupAdded)
                {
                    var textFields = document["weights"].AsBsonDocument.Elements.Select(e => e.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
                    normalized.Add("$text", new BsonArray(textFields));
                    textGroupAdded = true;
                }

                continue;
            }

            normalized.Add(element);
        }

        return normalized;
    }

    /// <summary>
    /// Reads the existing side's actual, server-resolved <c>weights</c> document for a text index, sorted the
    /// same way as <see cref="NormalizeDesiredWeights"/> so the two sides compare equal when they agree. Returns
    /// <see langword="null"/> for a non-text index (no <c>weights</c> field at all).
    /// </summary>
    private static BsonDocument? NormalizeExistingWeights(BsonDocument document)
    {
        if (!document.Contains("weights"))
        {
            return null;
        }

        var weights = document["weights"].AsBsonDocument;
        var normalized = new BsonDocument();
        foreach (var name in weights.Names.OrderBy(name => name, StringComparer.Ordinal))
        {
            normalized.Add(name, weights[name]);
        }

        return normalized;
    }

    /// <summary>
    /// Normalizes a rendered key specification for comparison. A requested text index renders its caller-facing
    /// <c>"text"</c> marker (e.g. <c>{Field: "text"}</c>), while <c>listIndexes</c> reports the server's internal
    /// <c>_fts</c>/<c>_ftsx</c> storage representation with the indexed field names moved into a separate
    /// <c>weights</c> document (see <see cref="NormalizeExistingKeys"/>). Both sides are rendered to the same
    /// shape here so a text index can be identified like any other index, instead of never matching. The
    /// <c>$text</c> marker replaces the text field(s) in place, at the position of the first one encountered
    /// (MongoDB requires every text field in a compound index to be consecutive), rather than being appended at
    /// the end: key position is part of the compound index contract, so a prefix field
    /// (e.g. <c>{prefix:1, content:"text"}</c>) and a suffix field (e.g. <c>{content:"text", prefix:1}</c>) must
    /// not compare equal.
    /// </summary>
    private static BsonDocument NormalizeRequestedKeys(BsonDocument renderedKeys, IReadOnlyList<String> textFields)
    {
        if (textFields.Count == 0)
        {
            return renderedKeys;
        }

        var normalized = new BsonDocument();
        var textGroupAdded = false;
        foreach (var element in renderedKeys.Elements)
        {
            if (element.Value.IsString && element.Value.AsString == "text")
            {
                if (!textGroupAdded)
                {
                    normalized.Add("$text", new BsonArray(textFields.OrderBy(name => name, StringComparer.Ordinal)));
                    textGroupAdded = true;
                }

                continue;
            }

            normalized.Add(element);
        }

        return normalized;
    }

    private static Boolean NullableDocumentEquals(BsonDocument? a, BsonDocument? b)
        => a is null ? b is null : b is not null && a.Equals(b);

    private static ExistingIndex ParseExisting(BsonDocument document)
    {
        var keys = NormalizeExistingKeys(document);
        var isGeo2D = Is2DIndex(document["key"].AsBsonDocument);

        var definition = new IndexDefinition(
            keys,
            document.Contains("unique") && document["unique"].ToBoolean(),
            document.Contains("sparse") && document["sparse"].ToBoolean(),
            document.Contains("partialFilterExpression") ? document["partialFilterExpression"].AsBsonDocument : null,
            document.Contains("collation") ? NormalizeExistingCollation(document["collation"].AsBsonDocument) : null,
            document.Contains("expireAfterSeconds") ? TimeSpan.FromSeconds(document["expireAfterSeconds"].ToDouble()) : null,
            NormalizeExistingWeights(document),
            document.Contains("default_language") ? document["default_language"].AsString : null,
            document.Contains("language_override") ? document["language_override"].AsString : null,
            document.Contains("textIndexVersion") ? document["textIndexVersion"].ToInt32() : null,
            document.Contains("wildcardProjection") ? document["wildcardProjection"].AsBsonDocument : null,
            isGeo2D ? document.Contains("bits") ? document["bits"].ToInt32() : 26 : null,
            isGeo2D ? document.Contains("min") ? document["min"].ToDouble() : -180.0 : null,
            isGeo2D ? document.Contains("max") ? document["max"].ToDouble() : 180.0 : null,
            document.Contains("hidden") && document["hidden"].ToBoolean(),
            document.Contains("storageEngine") ? document["storageEngine"].AsBsonDocument : null);

        return new ExistingIndex(document["name"].AsString, definition);
    }

    /// <summary>
    /// Resolves the server's true effective document for <paramref name="collation"/> by actually asking the
    /// server, instead of guessing: creates a throwaway index with exactly the requested <paramref name="collation"/>
    /// (including <see langword="null"/>, which inherits the collection's own default collation, same as the real
    /// desired model would) on a freshly generated GUID field name that cannot collide with any real index's keys,
    /// reads back its resolved <c>collation</c> document from <c>listIndexes</c> (filled in with every
    /// locale-specific default field the server applies, which this library cannot reproduce locally), and drops
    /// the throwaway index again. This only runs when <see cref="RequiresCollationProbe"/> says the literal value
    /// cannot be trusted, which only happens once an actual index conflict has already been caught, never on the
    /// ordinary no-conflict path, so the extra round trip is incidental next to the conflict's own
    /// drop-and-recreate cost.
    /// </summary>
    private static async Task<BsonDocument?> ProbeEffectiveCollationAsync<TDocument>(Collation? collation,
                                                                                     Func<CreateIndexModel<TDocument>, CancellationToken, Task<String>> createProbe,
                                                                                     Func<CancellationToken, Task<IAsyncCursor<BsonDocument>>> listIndexes,
                                                                                     Func<String, CancellationToken, Task> dropOneIfExists,
                                                                                     CancellationToken cancellationToken)
    {
        var probeName = $"__chaosMongoCollationProbe_{Guid.NewGuid():N}";
        var probeField = $"__chaosMongoCollationProbeField_{Guid.NewGuid():N}";
        var probeKeys = new BsonDocumentIndexKeysDefinition<TDocument>(new BsonDocument(probeField, 1));
        var probeModel = new CreateIndexModel<TDocument>(probeKeys, new CreateIndexOptions
        {
            Name = probeName,
            Collation = collation,
            Sparse = true
        });

        try
        {
            await createProbe(probeModel, cancellationToken);
            var indexes = await ListExistingIndexesAsync(listIndexes, cancellationToken);
            return indexes.FirstOrDefault(index => index.Name == probeName).Definition.Collation;
        }
        finally
        {
            // The server may have already created the probe by the time a cancellation is observed by this
            // method, whether during createProbe itself or while reading its resolved collation back via
            // listIndexes, so cleanup always runs here regardless of how the try block above completed. It never
            // reuses cancellationToken, which may already be canceled at this point: that would make the cleanup
            // drop itself an immediate no-op and leave the throwaway index behind forever. A separate, bounded,
            // never-already-canceled context is used instead.
            await DropProbeBestEffortAsync(probeName, dropOneIfExists);
        }
    }

    /// <summary>
    /// Decides whether the model's requested <c>Collation</c> needs to be resolved against the server (via
    /// <see cref="ProbeEffectiveCollationAsync{TDocument}"/>) to get its true effective document, instead of being
    /// compared literally. An explicit <c>simple</c> collation never needs resolving: it is the one collation
    /// value guaranteed not to be subject to any locale-specific tailoring, and <c>listIndexes</c> omits the field
    /// for it, the same as an index with no collation at all. Any other explicit collation always needs
    /// resolving, because MongoDB fills every field the caller left unset from locale-specific defaults (for
    /// example <c>Backwards</c> defaults to <c>true</c>, not <c>false</c>, for some locales) that cannot be
    /// reproduced locally. An omitted collation only needs resolving when at least one existing index sharing the
    /// same key specification stores an actual (non-null) collation: only then can the collection's own default
    /// collation, which an omitted <c>Collation</c> inherits, actually change which existing index is the real
    /// match (see the destructive drop this guards against: an unrelated explicitly-simple sibling on the same
    /// keys must never be mistaken for the index that actually inherited a non-simple collection default). The
    /// common case, no coexisting same-key index carries any collation at all, never pays the resolution's cost.
    /// </summary>
    private static Boolean RequiresCollationProbe(Collation? collation, BsonDocument desiredKeys, IReadOnlyList<ExistingIndex> indexes)
        => collation is not null
            ? collation.Locale != "simple"
            : indexes.Any(index => index.Definition.Keys.Equals(desiredKeys) && index.Definition.Collation is not null);

    private static ConflictResolution ResolveCandidate(IReadOnlyList<ExistingIndex> indexes, String requestedName, IndexDefinition desired)
    {
        var satisfiedName = FindFullySatisfyingIndexName(indexes, requestedName, desired);
        if (satisfiedName is not null)
        {
            return ConflictResolution.Satisfied(satisfiedName);
        }

        var sameName = indexes.FirstOrDefault(index => index.Name == requestedName);
        if (sameName.Name is not null)
        {
            return ConflictResolution.Candidate(sameName.Name);
        }

        var identityMatches = indexes.Where(index => IdentityEquals(index.Definition, desired)).ToList();
        return identityMatches.Count == 1
            ? ConflictResolution.Candidate(identityMatches[0].Name)
            : ConflictResolution.Unresolved;
    }

    /// <summary>
    /// Resolves an <c>IndexOptionsConflict</c>/<c>IndexKeySpecsConflict</c> by locating the index that actually
    /// causes it, instead of trusting the requested name as the drop target, and tolerates a concurrent caller
    /// having already resolved the same conflict.
    /// </summary>
    private static async Task<String?> ResolveConflictAsync<TDocument>(IMongoIndexManager<TDocument> indexManager,
                                                                       CreateIndexModel<TDocument> model,
                                                                       String requestedName,
                                                                       MongoCommandException exception,
                                                                       Func<CancellationToken, Task<IAsyncCursor<BsonDocument>>> listIndexes,
                                                                       Func<String, CancellationToken, Task> dropOneIfExists,
                                                                       Func<Task<String>> createOne,
                                                                       Func<CreateIndexModel<TDocument>, CancellationToken, Task<String>> createProbe,
                                                                       CancellationToken cancellationToken)
    {
        var indexes = await ListExistingIndexesAsync(listIndexes, cancellationToken);
        var desired = await BuildDesiredDefinitionAsync(indexManager, model, indexes, createProbe, listIndexes, dropOneIfExists, cancellationToken, exception);
        var resolution = ResolveCandidate(indexes, requestedName, desired);

        if (resolution is { Kind: ConflictResolutionKind.Satisfied, Name: { } satisfiedName })
        {
            return satisfiedName;
        }

        if (resolution is { Kind: ConflictResolutionKind.Candidate, Name: { } candidateName })
        {
            await dropOneIfExists(candidateName, cancellationToken);
        }

        return await RetryOrRecoverAsync(createOne, listIndexes, requestedName, desired, exception, cancellationToken);
    }

    /// <summary>
    /// Retries the creation once, tolerating a concurrent caller that resolved the same conflict between this
    /// call's own listing/drop and this retry: if the retry still conflicts, the desired index may already exist
    /// under the requested name because a concurrent caller finished first, in which case that index is returned
    /// instead of throwing. Only when the desired state is still not observed is the original exception rethrown.
    /// </summary>
    private static async Task<String?> RetryOrRecoverAsync(Func<Task<String>> createOne,
                                                           Func<CancellationToken, Task<IAsyncCursor<BsonDocument>>> listIndexes,
                                                           String requestedName,
                                                           IndexDefinition desired,
                                                           MongoCommandException exception,
                                                           CancellationToken cancellationToken)
    {
        try
        {
            return await createOne();
        }
        catch (MongoCommandException e) when (IsIndexConflict(e))
        {
            var indexes = await ListExistingIndexesAsync(listIndexes, cancellationToken);
            var satisfiedName = FindFullySatisfyingIndexName(indexes, requestedName, desired);
            if (satisfiedName is not null)
            {
                return satisfiedName;
            }

            throw exception;
        }
    }

    private readonly record struct ConflictResolution(ConflictResolutionKind Kind, String? Name)
    {
        public static ConflictResolution Unresolved { get; } = new(ConflictResolutionKind.Unresolved, null);

        public static ConflictResolution Candidate(String name) => new(ConflictResolutionKind.Candidate, name);

        public static ConflictResolution Satisfied(String name) => new(ConflictResolutionKind.Satisfied, name);
    }

    private enum ConflictResolutionKind
    {
        Satisfied,
        Candidate,
        Unresolved
    }

    private readonly record struct ExistingIndex(String Name, IndexDefinition Definition);

    /// <summary>
    /// The rendered key specification plus the normalized options that participate in identity (<see cref="Unique"/>,
    /// <see cref="Sparse"/>, <see cref="PartialFilter"/>, <see cref="Collation"/>) and effective (identity plus
    /// <see cref="ExpireAfter"/>, <see cref="Hidden"/>, <see cref="StorageEngine"/>, <see cref="Weights"/>,
    /// <see cref="DefaultLanguage"/>, <see cref="LanguageOverride"/>, <see cref="TextIndexVersion"/>,
    /// <see cref="WildcardProjection"/>, <see cref="Bits"/>, <see cref="Min"/> and <see cref="Max"/>) comparisons,
    /// for either a desired model or an existing index. <see cref="Collation"/> is always the fully resolved,
    /// effective document on both sides (see <see cref="BuildDesiredDefinitionAsync{TDocument}"/> and
    /// <see cref="ParseExisting"/>), never a literal, partially specified one.
    /// </summary>
    private readonly record struct IndexDefinition(
        BsonDocument Keys,
        Boolean Unique,
        Boolean Sparse,
        BsonDocument? PartialFilter,
        BsonDocument? Collation,
        TimeSpan? ExpireAfter,
        BsonDocument? Weights,
        String? DefaultLanguage,
        String? LanguageOverride,
        Int32? TextIndexVersion,
        BsonDocument? WildcardProjection,
        Int32? Bits,
        Double? Min,
        Double? Max,
        Boolean Hidden,
        BsonDocument? StorageEngine);
}
