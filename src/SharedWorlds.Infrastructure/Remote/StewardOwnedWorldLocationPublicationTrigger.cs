namespace SharedWorlds.Infrastructure.Remote;

/// <summary>
/// Coalesces canonical-storage change signals into at most one running publication pass and one pending
/// follow-up pass. It never retries on its own: failed exact operations remain durable in the journal,
/// while a later mutation, authentication activation, or process restart supplies the next attempt.
/// </summary>
public sealed class StewardOwnedWorldLocationPublicationTrigger : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<CancellationToken, Task> _publishAsync;
    private readonly Action<Exception>? _failureObserver;
    private readonly CancellationTokenSource _lifetime = new();

    private Task? _worker;
    private Exception? _lastFailure;
    private bool _requested;
    private bool _disposed;

    public StewardOwnedWorldLocationPublicationTrigger(
        Func<CancellationToken, Task> publishAsync,
        Action<Exception>? failureObserver = null)
    {
        ArgumentNullException.ThrowIfNull(publishAsync);
        _publishAsync = publishAsync;
        _failureObserver = failureObserver;
    }

    public Exception? LastFailure
    {
        get
        {
            lock (_sync)
            {
                return _lastFailure;
            }
        }
    }

    public void Request()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _requested = true;
            _worker ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    _worker = null;
                    return;
                }

                if (!_requested)
                {
                    _worker = null;
                    return;
                }

                _requested = false;
            }

            try
            {
                await _publishAsync(_lifetime.Token);
                lock (_sync)
                {
                    _lastFailure = null;
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                lock (_sync)
                {
                    _worker = null;
                }

                return;
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _lastFailure = exception;
                }

                if (_failureObserver is not null)
                {
                    try
                    {
                        _failureObserver(exception);
                    }
                    catch (Exception observerException)
                    {
                        lock (_sync)
                        {
                            _lastFailure = new AggregateException(
                                "Owned-World location publication and its failure observer both failed.",
                                exception,
                                observerException);
                        }
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _requested = false;
            _lifetime.Cancel();
        }
    }
}
