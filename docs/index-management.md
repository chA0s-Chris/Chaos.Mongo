# Index Management

Chaos.Mongo extends the MongoDB driver's index manager with `CreateOneOrUpdateAsync`. It creates a missing index and recreates an existing named index when its specification differs from the desired definition.

```csharp
var collection = mongo.GetCollection<User>();
var index = new CreateIndexModel<User>(
    Builders<User>.IndexKeys.Ascending(user => user.Email),
    new CreateIndexOptions
    {
        Name = "email_unique",
        Unique = true
    });

await collection.Indexes.CreateOneOrUpdateAsync(
    index,
    cancellationToken: cancellationToken);
```

Explicit names make it possible to compare and replace an existing definition predictably. Index creation is commonly placed in an idempotent [database configurator](configurators.md) or [migration](migrations.md), depending on whether the change is startup configuration or a versioned database transition.

### Resolving a Conflicting Index

When the driver reports an `IndexOptionsConflict` or `IndexKeySpecsConflict`, `CreateOneOrUpdateAsync` locates the existing index that is actually causing the conflict by its key specification and options, not by the name the failed command happened to request. This means renaming an index while keeping the same key specification works correctly: the old index is found and dropped even though the new name never existed, and an unrelated index that happens to share the same keys under a different name (for example a unique and a non-unique index coexisting on the same fields) is left untouched.

Two application instances that start at the same time and both try to resolve the same conflict are tolerated: dropping an index that a concurrent caller already dropped is a no-op, and if neither the old nor the new index can be found immediately after the conflict (because a concurrent caller is still mid-flight), the creation is retried once before giving up. Only when the conflicting index still cannot be identified or observed after this retry does `CreateOneOrUpdateAsync` rethrow the original `MongoCommandException`.

A text index is matched the same way: the server stores a text index's key specification as `{_fts: "text", _ftsx: 1}` with the indexed field names moved into a separate `weights` document, so the lookup compares the actual indexed fields rather than this internal representation. The indexed field set is derived from both the key specification's `"text"`-marked fields and `CreateIndexOptions.Weights`: MongoDB accepts (and actually indexes) a field declared only through `Weights`, even when it is absent from the caller-facing key document, so such a field is never silently dropped from the comparison. This also preserves the text component's position relative to the compound key's other fields: a non-text field is either a prefix or a suffix of the text group, and moving it across that group (for example from `{other: 1, field: "text"}` to `{field: "text", other: 1}`) is a genuine key-specification change, not a no-op. Each field's weight, `DefaultLanguage`, `LanguageOverride`, and `TextIndexVersion` are also compared, each defaulting to the server's own documented default (`1` per field, `"english"`, `"language"`, and `3`, respectively) for whichever of them the caller did not set explicitly, so a same-name text index whose weights (including a weight introduced only through `Weights`), language, language-override field, or version actually changed is replaced rather than mistaken for an already-satisfied request.

A wildcard index's `WildcardProjection` is compared the same way: a same-name wildcard index whose projection actually changed is replaced rather than mistaken for an already-satisfied request.

A legacy 2d index's `Bits`, `Min`, and `Max` are also compared, each defaulting to the server's own documented default (`26`, `-180`, and `180`, respectively) for whichever of them the caller did not set explicitly, so a same-name 2d index whose precision or bounds actually changed is replaced rather than mistaken for an already-satisfied request.

`Hidden` and `StorageEngine` are also compared regardless of index type, `Hidden` defaulting to the server's documented default of `false` for whichever side did not set it explicitly: a same-name index whose visibility to the query planner or storage engine configuration actually changed is replaced rather than mistaken for an already-satisfied request.

A `Collation` is compared by its true effective document, not by only the fields the caller happened to set. An explicit `simple` collation (or no collation at all) still matches an existing index with no collation, the way `listIndexes` reports it. Any other collation — whether entirely omitted (inheriting the collection's own default collation) or explicitly set but only partially (leaving fields for MongoDB to fill in from locale-specific defaults, which differ per locale and cannot be reproduced locally) — is resolved against the server: `CreateOneOrUpdateAsync` creates a throwaway index with that exact collation on a key that cannot collide with any real index, reads back the fully resolved collation document the server actually stored for it, and drops the throwaway index again, using an independent, bounded cleanup step so the throwaway index is removed even if the caller's own cancellation token is triggered mid-probe. This only happens once a real conflict has already been caught (never on the ordinary no-conflict path), and only when the literal, unresolved value cannot be trusted — for an omitted collation, only when some existing index sharing the same keys actually stores a collation, so the common case of a collection without any custom default collation never pays for it. This means renaming an index that inherited a non-simple collection default collation now works correctly, and an unrelated index that happens to share the same keys under an explicitly different collation (including an explicitly `simple` one) is never mistaken for it and dropped.

Resolving the collation this way needs one spare index slot in the target collection, because the throwaway probe is itself a real index. If the collection is already at MongoDB's 64-index limit when a conflict requires this resolution, the probe cannot be created, and `CreateOneOrUpdateAsync` throws the original `IndexOptionsConflict`/`IndexKeySpecsConflict` instead of silently succeeding or leaving the collection in an inconsistent state. A rename that would otherwise keep the final index count unchanged (dropping one index and creating another) still fails in this specific case; free an index slot first if this applies.

## Dropping an Index

`DropOneIfExistsAsync` drops a named index and ignores the error when the index or its collection does not already exist, so retiring an index stays idempotent:

```csharp
await collection.Indexes.DropOneIfExistsAsync(
    "email_unique",
    cancellationToken: cancellationToken);
```

Overloads accept a `DropIndexOptions` and a `IClientSessionHandle`, matching the driver's own `DropOneAsync` overloads. Any other error, such as an invalid index name, still throws `MongoCommandException`.
