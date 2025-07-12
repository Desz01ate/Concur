# Concur Development Guidelines

This document provides a comprehensive guide for developers contributing to the Concur project. It covers the project's overview, core functionality, structure, and the established coding conventions to ensure consistency and maintainability.

---

## 1. Project Overview

Concur is a lightweight C# library designed to bring Go-inspired concurrency patterns to the .NET ecosystem. It provides a simple, expressive API for managing concurrent operations, making it easier to write clean, readable, and maintainable asynchronous code.

The primary goal of Concur is to offer a higher-level abstraction over .NET's built-in concurrency primitives like `Task` and `Channel<T>`, without sacrificing performance. It is ideal for developers who appreciate the simplicity of Go's concurrency model and want to apply similar patterns in their C# applications.

---

## 2. Core Functionality

Concur is built around three fundamental concepts from Go:

-   **Goroutine (`Go`)**: A lightweight, concurrent function. In Concur, you create one using the `Go()` method. It can be a synchronous action or an `async` task that runs in the background without blocking the caller.
-   **Channel (`IChannel<T>`)**: A typed, thread-safe queue that allows goroutines to communicate safely. One goroutine writes data to the channel, and another reads from it, ensuring safe data transfer without explicit locks.
-   **WaitGroup**: A synchronization aid that allows your code to block and wait until a collection of goroutines has finished executing.

---

## 3. Project Structure

The project is organized into the following key directories and files:

```
/
├── .github/                # GitHub Actions workflows for CI/CD
│   └── workflows/
│       ├── build-and-test.yml
│       └── publish.yml
├── src/
│   ├── Concur.sln            # Visual Studio Solution
│   ├── Concur/               # The main library project
│   │   ├── Concur.csproj
│   │   ├── ConcurRoutine.cs  # Static class with Go() methods
│   │   ├── WaitGroup.cs      # WaitGroup implementation
│   │   ├── Abstractions/     # Channel interfaces
│   │   └── Implementations/  # Default channel implementation
│   ├── Concur.Benchmark/     # Performance benchmarks
│   └── Concur.Tests/         # Unit tests
├── .gitignore
├── LICENSE
└── README.md
```

-   **`src/Concur/`**: Contains the core library code.
    -   `ConcurRoutine.cs`: The entry point for all `Go()` methods.
    -   `WaitGroup.cs`: The `WaitGroup` implementation.
    -   `Abstractions/IChannel.cs`: Defines the public interface for channels.
    -   `Implementations/DefaultChannel.cs`: The default, `System.Threading.Channels.Channel<T>`-based implementation of `IChannel<T>`.
-   **`src/Concur.Tests/`**: Contains unit tests for the library, written using xUnit.
-   **`src/Concur.Benchmark/`**: Contains performance benchmarks written with BenchmarkDotNet.
-   **`.github/workflows/`**: Defines the CI/CD pipelines for building, testing, and publishing the library.

---

## 4. Development Guidelines

### Code Style & Formatting

-   **C# Version**: The project uses modern C# features. Stick to the language version specified in the `.csproj` files.
-   **Naming Conventions**:
    -   Use PascalCase for class, method, property, and event names.
    -   Use camelCase for local variables and method parameters.
    -   Interfaces should be prefixed with `I` (e.g., `IChannel`).
    -   Private fields should not have a special prefix (e.g., `count`, not `_count`).
-   **Braces**: Use Allman-style braces, where each brace begins on a new line.

    ```csharp
    // Correct
    public void MyMethod()
    {
        // ...
    }

    // Incorrect
    public void MyMethod() {
        // ...
    }
    ```

-   **`using` Directives**:
    -   Place `using` directives at the top of the file, outside the `namespace` block.
    -   The `using static Concur.ConcurRoutine;` is encouraged in consumer code for easy access to `Go()`.
-   **XML Documentation**:
    -   All public members (`classes`, `methods`, `properties`) must have clear and concise XML documentation comments.
    -   Use `<summary>`, `<param>`, `<returns>`, and `<remarks>` tags where appropriate.
    -   Provide `<example>` blocks for complex or important methods, as seen in `ConcurRoutine.cs`.

### Asynchronous Programming

-   **`async`/`await`**: Use `async`/`await` for all asynchronous operations. Avoid using `.Result` or `.Wait()` as it can lead to deadlocks. The only exception is the `WaitGroup.Wait()` method, which is provided for convenience in synchronous contexts.
-   **`Task.Run`**: The `Go()` methods are built on `Task.Run`. This is the intended mechanism for offloading work to a background thread.
-   **Exception Handling**:
    -   Exceptions within a `Go` routine are caught and delegated to the static `ConcurRoutine.OnException` handler. This prevents background tasks from crashing the application.
    -   Contributors should ensure that all new `Go` overloads include a `try...catch` block that calls `OnException`.
    -   When using a `WaitGroup`, a `finally` block must be used to guarantee that `wg.Done()` is called, even if an exception occurs.

### Unit Testing

-   **Framework**: Tests are written using **xUnit**.
-   **Location**: For every new feature or bug fix, corresponding unit tests must be added in the `Concur.Tests` project.
-   **Assertions**: Use the built-in `Assert` methods from xUnit.
-   **Test Naming**: Test methods should be named descriptively, following the pattern `MethodName_WithState_ExpectsResult`. For example: `Go_WithWaitGroup_WithAction_ExecutesAction`.

### Commits and Pull Requests

-   **Commit Messages**: Write clear and concise commit messages. The first line should be a short summary (e-g-, `Feat: Add bounded channel support to Go<T>`).
-   **Pull Requests**:
    -   Before submitting a pull request, ensure that all existing and new tests pass.
    -   Run the benchmarks if your changes could impact performance.
    -   Update the `README.md` or other documentation if you are adding or changing functionality.
    -   Reference any relevant issues in your pull request description.

---

By following these guidelines, we can maintain a high-quality, consistent, and easy-to-contribute-to codebase.
