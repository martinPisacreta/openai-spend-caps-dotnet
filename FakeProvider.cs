using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace LlmIntegration;

// Local test infrastructure only. Never selected through a public request.
public sealed record FakeReply(
    int StatusCode, string Body, TimeSpan Delay, string? RetryAfter = null);

public sealed class FakeProvider : IAsyncDisposable
{
    private readonly WebApplication application;
    private readonly ConcurrentQueue<FakeReply> replies = new();
    private readonly ConcurrentQueue<string> requestBodies = new();
    private int attempts;

    private FakeProvider(WebApplication application)
    {
        this.application = application;
    }

    public Uri BaseAddress { get; private set; } = null!;
    public int Attempts => Volatile.Read(ref attempts);
    public string[] RequestBodies => requestBodies.ToArray();

    public void Enqueue(FakeReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.StatusCode < 200 || reply.StatusCode > 599)
            throw new ArgumentOutOfRangeException(nameof(reply));
        if (reply.Delay < TimeSpan.Zero || reply.Delay > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(reply));
        replies.Enqueue(reply);
    }

    public static async Task<FakeProvider> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                EnvironmentName = "Testing"
            });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 65536;
            options.Listen(IPAddress.Loopback, 0);
        });

        var app = builder.Build();
        var provider = new FakeProvider(app);
        app.Run(provider.HandleAsync);
        try
        {
            await app.StartAsync(cancellationToken);
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features
                .Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("Missing listener address.");
            var address = new Uri(addresses.Single());
            if (address.Scheme != "http" || address.Host != IPAddress.Loopback.ToString())
                throw new InvalidOperationException("Fake provider must use IPv4 loopback.");
            provider.BaseAddress = new Uri(address.AbsoluteUri.TrimEnd('/') + "/");
            return provider;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (context.Request.Method != "POST" ||
            context.Request.Path != "/v1/chat/completions")
        {
            context.Response.StatusCode = 404;
            return;
        }

        try
        {
            // Store synthetic request bodies, never authorization headers.
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var count = await context.Request.Body.ReadAsync(
                    chunk.AsMemory(), context.RequestAborted);
                if (count == 0) break;
                if (buffer.Length + count > 65536)
                {
                    context.Response.StatusCode = 413;
                    return;
                }
                buffer.Write(chunk, 0, count);
            }
            requestBodies.Enqueue(
                System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            Interlocked.Increment(ref attempts);

            if (!replies.TryDequeue(out var reply))
            {
                context.Response.StatusCode = 503;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    "{\"error\":{\"message\":\"No scripted reply\"}}",
                    context.RequestAborted);
                return;
            }

            await Task.Delay(reply.Delay, context.RequestAborted);
            context.Response.StatusCode = reply.StatusCode;
            context.Response.ContentType = "application/json";
            if (reply.RetryAfter is not null)
                context.Response.Headers["Retry-After"] = reply.RetryAfter;
            await context.Response.WriteAsync(reply.Body, context.RequestAborted);
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested)
        {
            // Expected when the client exercises its overall timeout.
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == 413 && !context.Response.HasStarted)
        {
            context.Response.StatusCode = 413;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await application.StopAsync(timeout.Token);
        }
        finally
        {
            await application.DisposeAsync();
        }
    }
}
