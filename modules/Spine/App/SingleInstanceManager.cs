using System;
using System.Threading;

namespace DadBoard.App;

sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = "Global\\DadBoard.SingleInstance";
    private const string ActivateEventName = "Global\\DadBoard.Activate";
    private const string ShutdownEventName = "Global\\DadBoard.Shutdown";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly EventWaitHandle _shutdownEvent;
    private Thread? _listenerThread;
    private bool _disposed;

    private SingleInstanceManager(Mutex mutex, EventWaitHandle activateEvent, EventWaitHandle shutdownEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _shutdownEvent = shutdownEvent;
    }

    public static SingleInstanceManager? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);

        if (!createdNew)
        {
            mutex.Dispose();
            activateEvent.Dispose();
            shutdownEvent.Dispose();
            return null;
        }

        return new SingleInstanceManager(mutex, activateEvent, shutdownEvent);
    }

    public static void SignalActivate()
    {
        try
        {
            using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            activateEvent.Set();
        }
        catch
        {
        }
    }

    public static void SignalShutdown()
    {
        try
        {
            using var shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
            shutdownEvent.Set();
        }
        catch
        {
        }
    }

    public static SingleInstanceManager? TryAcquireWithRetry(TimeSpan timeout, TimeSpan retryDelay)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var instance = TryAcquire();
            if (instance != null)
            {
                return instance;
            }

            Thread.Sleep(retryDelay);
        }

        return null;
    }

    public void BeginListen(Action onActivate, Action onShutdown)
    {
        if (_listenerThread != null)
        {
            return;
        }

        _listenerThread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _activateEvent, _shutdownEvent };
            while (true)
            {
                var signaled = WaitHandle.WaitAny(handles);
                if (_disposed)
                {
                    return;
                }

                try
                {
                    if (signaled == 0)
                    {
                        onActivate();
                    }
                    else if (signaled == 1)
                    {
                        onShutdown();
                    }
                }
                catch
                {
                }
            }
        })
        {
            IsBackground = true,
            Name = "DadBoard.InstanceListener"
        };
        _listenerThread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _activateEvent.Set();
            _shutdownEvent.Set();
        }
        catch
        {
        }

        _activateEvent.Dispose();
        _shutdownEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
