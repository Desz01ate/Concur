# Concur

A lightweight C# library providing Go-inspired concurrency patterns for .NET applications.

## Features

- Go-style goroutines for asynchronous execution
- Channel-based communication between concurrent tasks
- Simple, intuitive API inspired by Go's concurrency model

## Usage

### Basic Concurrency

```csharp
using static ConcurRoutine;

// Run a synchronous action concurrently
Go(() => {
    Console.WriteLine("Running in background");
});

// Run an async function concurrently,
// This allows the async lambda to run independently without blocking
// the calling thread.
Go(async () => {
    await Task.Delay(1000);
    Console.WriteLine("Async work completed");
});
```

### Using Channels

```csharp
using static ConcurRoutine;

// Create a channel and producer
var numbers = Go<int>(async writer => {
    for (int i = 0; i < 10; i++) {
        await writer.WriteAsync(i);
        await Task.Delay(100);
    }
    
    // the producer is responsible for completing the channel.
    writer.TryComplete();
});

// Consume values
await foreach (var number in numbers.ReadAllAsync()) {
    Console.WriteLine($"Received: {number}");
}
```

### Error Handling

By default, exceptions in concurrent tasks are suppressed and logged only in debug mode. For production environments, you should configure proper error handling:

```csharp
// Configure with your logging framework
using Microsoft.Extensions.Logging;
using static ConcurRoutine;

// In your application startup
ConcurRoutine.OnException = exception => 
    logger.LogError(exception, "Error in background task");
```