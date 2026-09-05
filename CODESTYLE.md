## Code Style Guidelines

The code style rules are described in the `.editorconfig` file.

### General
- Follow the [.NET Coding Conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use `PascalCase` for class names, method names, and public members
- Use `camelCase` for local variables, constants, and parameters
- Use `_camelCase` for private fields
- Use `PascalCase` for constant members and static readonly fields
- Use `IPascalCase` for interfaces
- Use meaningful and descriptive names
- Keep methods small and focused (ideally < 30 lines)

### Types and Constructors
- Classes always declare explicit constructors - primary constructors on classes are not permitted
- Assign constructor dependencies to `private readonly` fields (or to properties where the type exposes them)
- Positional records and positional record structs remain fine - the rule covers classes only

### Formatting
- Use 4 spaces for indentation (no tabs)
- Use `var` for local variables
- Use CLR types instead of C# aliases (Int32 vs int, String vs string, Boolean vs bool)
- Block bodies and expression-bodied members are both permitted
- Use string interpolation (`$"Hello {name}"`) over string concatenation
- Use `nameof()` when referencing parameters, properties, or methods
- Always use parentheses for clarity in expressions

### Documentation
- Add XML documentation for public APIs in reusable library projects. Application and executable projects do not require XML documentation merely because a type is public; document externally consumed contracts and non-obvious behavior where useful.
- When XML documentation is required, include:
    - Summary of what the member does
    - Parameters with `<param>` tags
    - Return values with `<returns>`
    - Exceptions with `<exception>`
- Example for methods:
  ```csharp
  /// <summary>
  /// Retrieves a patient by their unique identifier.
  /// </summary>
  /// <param name="patientId">The unique identifier of the patient.</param>
  /// <returns>The patient if found; otherwise, null.</returns>
  /// <exception cref="ArgumentException">Thrown when <paramref name="patientId"/> is empty.</exception>
  public async Task<Patient> GetPatientByIdAsync(string patientId)
  ```

- Use /// comments for non-trivial implementation details
- Prefer self-documenting code over excessive comments
- Document all public types and members in shared libraries

### Imports
- Sort usings alphabetically without placing `System.*` first
- Remove unused usings
- Do not separate usings into groups:
  ```csharp
  using Microsoft.Extensions.DependencyInjection;
  using MyProject.Features.Common;
  using System;
  using System.Collections.Generic;
  ```

### Error Handling
- Use specific exception types when possible
- Include meaningful error messages
- Use try-catch only when you can handle the exception
- Use `throw;` to rethrow exceptions without losing stack trace
- Use `ArgumentNullException.ThrowIfNull()` for null checks
- Use `String.IsNullOrEmpty()` for string validation

### Nullability

- Nullable reference types are enabled project-wide - keep them enabled and do not suppress nullable warnings
- Prefer guard clauses, validated locals, pattern matching, and nullable-flow attributes over the null-forgiving operator (`!`)
- Have validation return the proven values instead of forgiving them afterwards - `[MemberNotNull]` only covers members of the containing type, so it cannot carry a post-condition about a parameter's members
- Do not repeat `!` after a value has been validated - carry the proven value in one non-nullable local
- In tests, `!` is unnecessary after a FluentAssertions `Should().NotBeNull()` on the same expression, and fixture members assigned in `[SetUp]`/`[OneTimeSetUp]` need no `= null!` initializer because NUnit.Analyzers suppresses the warning
- Use `!` only for invariants nullable flow analysis cannot establish - framework-injected members and deliberately invalid `null!` inputs in argument-validation tests - and make such uses obvious from the surrounding code or add a short comment
- `dotnet jb inspectcode` reports redundant uses as `RedundantSuppressNullableWarningExpression`

### Async/Await
- Use `async`/`await` instead of `.Result` or `.Wait()`
- Suffix async methods with `Async` (e.g., `GetDataAsync`)
- Avoid `async void` (except for event handlers)
- Use `ValueTask<T>` for performance-critical paths
