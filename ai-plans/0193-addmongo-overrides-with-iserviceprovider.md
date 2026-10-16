# AddMongo Overrides with IServiceProvider

> Issue: [#193](https://github.com/chA0s-Chris/Chaos.Mongo/issues/193)

## Rationale

`AddMongo` configures `MongoOptions` through `Action<MongoOptions>` delegates that cannot access the dependency-injection container. Options that depend on registered services, for example `ConfigureClientSettings` wiring the MongoDB driver's `LoggingSettings` to the application's `ILoggerFactory`, currently require the undiscoverable `AddOptions<MongoOptions>().Configure<TDep>(…)` workaround.

The goal is a first-class way to configure `MongoOptions` with access to the `IServiceProvider`: new `AddMongo` overloads, as requested in the issue, plus a composable builder method that also covers entry points without a configure delegate, such as `AddMongo(IConfiguration, String)`.

## Acceptance Criteria

- [ ] `MongoBuilder.ConfigureOptions(Action<IServiceProvider, MongoOptions>)` is available after every `AddMongo` entry point, including `AddMongo(IConfiguration, String)`. It runs after the configuration applied by that entry point and returns the builder for chaining.
- [ ] New `AddMongo` overloads accept a required `Action<IServiceProvider, MongoOptions>` for the parameterless, connection-string, and `MongoUrl` entry points. The connection-string and `MongoUrl` overloads set `Url` and `DefaultDatabase` before invoking the delegate, consistent with the existing overloads.
- [ ] Options configured through the new APIs are still validated by `MongoOptionsValidation` and `ValidateOnStart`.
- [ ] Null delegates and null URLs throw `ArgumentNullException`, and null or whitespace connection strings throw `ArgumentException`, at the call site, consistent with the existing overloads.
- [ ] Existing usages keep compiling and behaving unchanged, including `AddMongo()`, `AddMongo(connectionString)`, `AddMongo(url)`, and `Action<MongoOptions>` lambdas. The only exception is the documented literal-`null` `configure` argument on the three-argument connection-string and `MongoUrl` overloads.
- [ ] Automated tests cover the new APIs, and merged line coverage remains at least 95%.
- [ ] `docs/configuration.md` (and `docs/getting-started.md` where appropriate) documents the new APIs with a DI-backed example. The docs state that the delegate runs when the options are first resolved (at startup under a generic host, because of `ValidateOnStart`) and that, for `IOptions<MongoOptions>` as used by the library, it receives the root provider.

## Technical Details

New public API (exact signatures, XML documentation required):

```csharp
// MongoBuilder (new)
public MongoBuilder ConfigureOptions(Action<IServiceProvider, MongoOptions> configure);

// ServiceCollectionExtensions (new overloads)
public static MongoBuilder AddMongo(this IServiceCollection services, Action<IServiceProvider, MongoOptions> configure);
public static MongoBuilder AddMongo(this IServiceCollection services, String connectionString, String? databaseName, Action<IServiceProvider, MongoOptions> configure);
public static MongoBuilder AddMongo(this IServiceCollection services, MongoUrl url, String? databaseName, Action<IServiceProvider, MongoOptions> configure);
```

- The delegate parameter order `(IServiceProvider, MongoOptions)` follows the repository's existing provider-aware APIs, such as `IntegrityAnchoringBuilder.OnAnchorCompleted` and `WithTimestampAuthority`, rather than the `(TOptions, TDep)` order of `OptionsBuilder.Configure<TDep>`.
- `ConfigureOptions` registers through `AddOptions<MongoOptions>().Configure<IServiceProvider>(…)`. The new `AddMongo` overloads delegate to the existing registration path and then to `ConfigureOptions`, so there is one provider-aware mechanism. Configure actions run in registration order, and validation runs after all of them.
- The delegate is invoked by the singleton `IOptions<MongoOptions>` factory and therefore receives the root provider. Resolving scoped services from it throws when scope validation is enabled. The XML documentation and user documentation must state this.
- The new delegate parameters are required, never optional, so `AddMongo()`, `AddMongo(connectionString)`, and `AddMongo(url)` stay unambiguous. Lambda arity distinguishes the new overloads from the existing `Action<MongoOptions>` overloads. Because C# requires optional parameters to come last, `databaseName` is a required, nullable parameter in the new overloads. No two-argument `(connectionString, configure)` or `(url, configure)` variants are added; `AddMongo(connectionString).ConfigureOptions(…)` covers that case.
- Accepted source break (pre-1.0): passing a literal `null` as `configure` to the existing three-argument connection-string or `MongoUrl` overloads becomes ambiguous. Label the PR `breaking`.
- Out of scope: provider-aware configuration for `WithQueue` and `WithOutbox`, tracked in [#194](https://github.com/chA0s-Chris/Chaos.Mongo/issues/194).
- Test scenarios that are not obvious from the criteria: the delegate resolves a service registered in the container; `ConfigureOptions` overrides values bound from `IConfiguration`; `ConfigureOptions` supplying the only `Url` lets validation succeed, while a missing `Url` still fails validation.
