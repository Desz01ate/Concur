# Concur.Extensions.AspNetCore Design

Date: 2026-04-11
Status: Draft for review

## 1. Goal

Create a new project, `Concur.Extensions.AspNetCore`, that makes `Concur.Context` a native request-scoped primitive in ASP.NET Core for both:
- MVC controller actions
- Minimal API handlers

The extension must allow users to choose `Context` instead of `CancellationToken` when they want shared cancellation/cause semantics, while keeping `CancellationToken` usage fully valid and unchanged.

## 2. Scope and Non-Goals

### In scope
- New extension package for ASP.NET Core integration.
- DI registration API for configuring where linked cancellation tokens come from.
- Global-on behavior after registration: users can request `Context` directly in action/endpoint parameters.
- Configurable failure policy for token-source evaluation.
- Operation name derivation from endpoint metadata by default.
- Dedicated project-local documentation for the extension.

### Out of scope
- Replacing native ASP.NET `CancellationToken` support.
- Changing core `Concur` cancellation semantics.
- Middleware-based mandatory pipeline hooks.

## 3. Public API

## 3.1 Service registration

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConcurContext(
        this IServiceCollection services,
        Action<ConcurContextOptions>? configure = null);
}
```

## 3.2 Options

```csharp
public sealed class ConcurContextOptions
{
    public ConcurContextFailureMode FailureMode { get; set; } = ConcurContextFailureMode.Strict;

    public Func<HttpContext, string?> OperationNameSelector { get; set; }
        = ConcurOperationNameSelectors.Default;

    public void AddHttpContextToken(Func<HttpContext, CancellationToken> selector);

    public void AddServiceToken<TService>(Func<TService, CancellationToken> selector)
        where TService : notnull;
}
```

```csharp
public enum ConcurContextFailureMode
{
    Strict,
    Lenient,
}
```

Rationale:
- `AddHttpContextToken` and `AddServiceToken<T>` are intentionally different names to avoid delegate-overload ambiguity.
- Token source configuration is explicit and composable.

## 4. Expected Developer Experience

### 4.1 Registration

```csharp
builder.Services.AddConcurContext(options =>
{
    options.AddHttpContextToken(http => http.RequestAborted);
    options.AddServiceToken<IHostApplicationLifetime>(lifetime => lifetime.ApplicationStopping);
    options.FailureMode = ConcurContextFailureMode.Strict;
});
```

### 4.2 Controller action

```csharp
[HttpGet("users")]
public async Task<IActionResult> GetUsers(Context context)
{
    await Task.Delay(100, context.CancellationToken);
    return Ok();
}
```

### 4.3 Minimal API

```csharp
app.MapGet("/users", async (Context context) =>
{
    await Task.Delay(100, context.CancellationToken);
    return Results.Ok();
});
```

Notes:
- Global-on means if registration exists, parameter binding should work for `Context` everywhere.
- Users can still request `CancellationToken` and skip `Context` entirely.

## 5. Runtime Semantics

## 5.1 Request-scoped context construction

Per request:
1. Resolve configured token sources.
2. Evaluate each source to a token.
3. Build a request operation name via `OperationNameSelector`.
4. Create context via core API:
   - no linked tokens: `Context.Background.WithCancel(operationName)`
   - linked tokens: `Context.Background.WithCancel(tokens, operationName)`

This directly leverages first-cancel-wins behavior in core `Context`.

## 5.2 Failure policy

### `Strict` (default)
- If any token source evaluation throws, fail context creation and let request fail.
- Surface a descriptive exception indicating which source failed.

### `Lenient`
- If a token source evaluation throws, ignore that source and continue.
- If all configured sources fail/produce non-cancelable tokens, still produce a valid cancellable child context from `Background`.

## 5.3 Operation name default

Default selector should attempt (in order):
1. Route pattern from endpoint metadata (`RouteEndpoint.RoutePattern.RawText`) when available.
2. Endpoint display name (`Endpoint.DisplayName`) when meaningful.
3. Fallback literal, e.g. `"http-request"`.

Goal: useful diagnostics with no extra app code.

## 6. Binding Architecture

## 6.1 Minimal APIs

Rely on DI inference for `Context` parameter resolution from request services.

## 6.2 MVC Controllers

Register MVC binding integration so `Context` action parameters resolve from request services without requiring `[FromServices]` decorations.

Implementation options (preferred order):
1. MVC model binder provider for `Context` that resolves scoped instance from `HttpContext.RequestServices`.
2. If needed, an application model convention to infer service binding for `Context`.

## 6.3 Lifecycle and disposal

`Context` is registered as scoped and therefore disposed automatically with the request scope. No custom middleware required.

## 7. Project Structure

Add:
- `src/Concur.Extensions.AspNetCore/Concur.Extensions.AspNetCore.csproj`
- `src/Concur.Extensions.AspNetCore/README.md`
- `src/Concur.Extensions.AspNetCore.Tests/Concur.Extensions.AspNetCore.Tests.csproj`
- `src/Concur.Extensions.AspNetCore.Tests/...` test files

Update:
- `src/Concur.sln` to include both new projects.
- Root `README.md` to add a short section linking to `src/Concur.Extensions.AspNetCore/README.md`.

Documentation rule from design discussion:
- Main README should only summarize and link.
- Detailed ASP.NET extension docs live in the extension project directory.

## 8. Testing Strategy

## 8.1 Unit tests

1. `AddConcurContext` registers scoped `Context`.
2. `AddHttpContextToken` linkage propagates cancellation.
3. `AddServiceToken<T>` linkage propagates cancellation.
4. Multiple token sources: any token cancels resulting context.
5. First-cancel-wins cause attribution is preserved.
6. Operation name default derivation follows selector fallback order.
7. `Strict` failure mode throws on source exceptions.
8. `Lenient` failure mode recovers and returns usable context.

## 8.2 Integration tests

Using `WebApplicationFactory`:
1. Minimal endpoint receives `Context` parameter.
2. Controller action receives `Context` parameter.
3. Cancel request token and verify work awaiting `context.CancellationToken` is canceled.
4. Optional case: `ApplicationStopping` token triggers cancellation when configured.

## 9. Packaging and Versioning

- Keep target framework aligned with repo convention (`$(FrameworkVersion)` / `net8.0`).
- Package ID: `Concur.Extensions.AspNetCore`.
- Extension package version starts aligned with next core release train unless release strategy later diverges.

## 10. Risks and Mitigations

1. MVC binding edge cases across ASP.NET versions.
- Mitigation: narrow supported framework target and test both controller and minimal API surfaces.

2. Hidden failures in token source delegates.
- Mitigation: explicit `FailureMode` with strict default, plus actionable exception text.

3. Ambiguous operation names.
- Mitigation: overrideable `OperationNameSelector` and deterministic default fallback order.

## 11. Acceptance Criteria

1. Apps can request `Context` directly in controller actions and minimal handlers after one registration call.
2. Token source configuration supports both `HttpContext`-based and DI-service-based providers.
3. Failure behavior is configurable and tested.
4. Context cancellation reflects configured external tokens, preserving first-cancel-wins cause behavior.
5. Extension has dedicated project-local documentation and root README link.
