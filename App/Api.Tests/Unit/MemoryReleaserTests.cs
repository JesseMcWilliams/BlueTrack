using BlueTrack.Api.Data;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-371: one compacting collection a few seconds after a large job; calls close together share it.</summary>
public class MemoryReleaserTests
{
    private sealed class ListLogger : ILogger<MemoryReleaser>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void ReleaseNow_Collects_AndLogsTheMemoryBeforeAndAfter()
    {
        var logger = new ListLogger();

        var (before, after) = new MemoryReleaser(logger).ReleaseNow("a test");

        Assert.True(before > 0 && after > 0);
        var message = Assert.Single(logger.Messages);
        Assert.StartsWith("Released memory after a test: private memory ", message);
    }

    [Fact]
    public async Task ReleaseSoon_CalledTwiceTogether_CollectsOnce_AfterTheDelay()
    {
        var logger = new ListLogger();
        var releaser = new MemoryReleaser(logger);

        releaser.ReleaseSoon("a pull");
        releaser.ReleaseSoon("a DNA import");
        Assert.Empty(logger.Messages);

        var deadline = DateTime.UtcNow + MemoryReleaser.Delay + TimeSpan.FromSeconds(20);
        while (logger.Messages.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);

        var message = Assert.Single(logger.Messages);
        Assert.StartsWith("Released memory after a pull:", message);
    }
}
