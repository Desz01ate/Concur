using Concur;
using Concur.Abstractions;
using static Concur.ConcurRoutine;

// Example 1: Basic usage with default exception handler
Console.WriteLine("=== Example 1: Basic Usage ===");
Go(() => throw new InvalidOperationException("This will be logged in DEBUG mode"));

await Task.Delay(100); // Give time for background task

// Example 2: Custom exception handler
Console.WriteLine("=== Example 2: Custom Exception Handler ===");

var options = new GoOptions
{
    ExceptionHandler = new CustomExceptionHandler(),
    OperationName = "DataProcessing",
    Metadata = new Dictionary<string, object?>
    {
        ["UserId"] = 123,
        ["RequestId"] = Guid.NewGuid()
    }
};

Go(() => throw new ArgumentException("Invalid data format"), options);

await Task.Delay(100); // Give time for background task

// Example 3: Silent exception handling
Console.WriteLine("=== Example 3: Silent Exception Handling ===");

var silentOptions = new GoOptions
{
    ExceptionHandler = SilentExceptionHandler.Instance,
    OperationName = "BackgroundCleanup"
};

Go(() => throw new InvalidOperationException("This will be silently ignored"), silentOptions);

await Task.Delay(100); // Give time for background task

// Example 4: Using with dependency injection
Console.WriteLine("=== Example 4: Dependency Injection Pattern ===");

var emailService = new EmailService(new LoggingExceptionHandler());
await emailService.SendEmailAsync("user@example.com", "Hello!");

Console.WriteLine("All examples completed!");

// Custom exception handler implementation
public class CustomExceptionHandler : IExceptionHandler
{
    public ValueTask HandleAsync(IExceptionContext context)
    {
        Console.WriteLine($"[CUSTOM] Exception in routine '{context.RoutineId}':");
        Console.WriteLine($"  Operation: {context.OperationName}");
        Console.WriteLine($"  Exception: {context.Exception.Message}");
        Console.WriteLine($"  Timestamp: {context.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        
        if (context.Metadata.Any())
        {
            Console.WriteLine("  Metadata:");
            foreach (var kvp in context.Metadata)
            {
                Console.WriteLine($"    {kvp.Key}: {kvp.Value}");
            }
        }
        
        return ValueTask.CompletedTask;
    }
}

// Another custom handler for demonstration
public class LoggingExceptionHandler : IExceptionHandler
{
    public ValueTask HandleAsync(IExceptionContext context)
    {
        Console.WriteLine($"[LOG] {context.Timestamp:HH:mm:ss} - {context.OperationName}: {context.Exception.Message}");
        return ValueTask.CompletedTask;
    }
}

// Example service using dependency injection
public class EmailService
{
    private readonly IExceptionHandler exceptionHandler;
    
    public EmailService(IExceptionHandler exceptionHandler)
    {
        this.exceptionHandler = exceptionHandler;
    }
    
    public async Task SendEmailAsync(string email, string message)
    {
        var options = new GoOptions
        {
            ExceptionHandler = exceptionHandler,
            OperationName = "SendEmail",
            Metadata = new Dictionary<string, object?> 
            { 
                ["EmailAddress"] = email,
                ["MessageLength"] = message.Length
            }
        };
        
        Go(() =>
        {
            // Simulate email sending that might fail
            if (email.Contains("invalid"))
            {
                throw new ArgumentException($"Invalid email address: {email}");
            }
            
            Console.WriteLine($"Email sent to {email}: {message}");
        }, options);
        
        await Task.Delay(50); // Give time for background task
    }
}