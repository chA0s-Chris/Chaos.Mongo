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

### Formatting
- Use 4 spaces for indentation (no tabs)
- Use `var` for local variables
- Use CLR types instead of C# aliases (Int32 vs int, String vs string, Boolean vs bool)
- Use expression-bodied members when they fit on one line
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

### Error Handling
- Use specific exception types when possible
- Include meaningful error messages
- Use try-catch only when you can handle the exception
- Use `throw;` to rethrow exceptions without losing stack trace
- Use `ArgumentNullException.ThrowIfNull()` for null checks
- Use `String.IsNullOrEmpty()` for string validation

### Async/Await
- Use `async`/`await` instead of `.Result` or `.Wait()`
- Suffix async methods with `Async` (e.g., `GetDataAsync`)
- Avoid `async void` (except for event handlers)
- Use `ValueTask<T>` for performance-critical paths
