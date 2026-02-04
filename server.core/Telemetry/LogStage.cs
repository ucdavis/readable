using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace server.core.Telemetry;

public static class LogStage
{
    public static IDisposable Begin(ILogger logger, string fileId, string stage, object? details = null, string kind = "Stage")
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation(
            "{kind} start: {fileId} stage={stage} details={details}",
            kind,
            fileId,
            stage,
            details);

        return new DisposableAction(() =>
        {
            logger.LogInformation(
                "{kind} end: {fileId} stage={stage} elapsedMs={elapsedMs}",
                kind,
                fileId,
                stage,
                sw.Elapsed.TotalMilliseconds);
        });
    }

    private sealed class DisposableAction : IDisposable
    {
        private readonly Action _onDispose;
        private int _disposed;

        public DisposableAction(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _onDispose();
        }
    }
}

