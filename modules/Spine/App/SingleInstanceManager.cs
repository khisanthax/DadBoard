using System;
using System.Threading;

namespace DadBoard.App;

sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = "Global\\DadBoard.SingleInstance";
    private const string ActivateEventName = "Global\\DadBoard.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private Thread? _listenerThread;
    private bool _disposed;

    private SingleInstanceManager(Mutex mutex, EventWaitHandle activateEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
    }

    public static SingleInstanceManager? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        if (!createdNew)
        {
            try
            {
                activateEvent.Set();
            }
            catch
            {
            }

            mutex.Dispose();
            activateEvent.Dispose();
            return null;
        }

        return new SingleInstanceManager(mutex, activateEvent);
    }

    public void BeginListen(Action onActivate)
    {
        if (_listenerThread != null)
        {
            return;
        }

        _listenerThread = new Thread(() =>
        {
            while (true)
            {
                _activateEvent.WaitOne();
                if (_disposed)
                {
                    return;
                }

                try
                {
                    onActivate();
                }
                catch
                {
                }
            }
        })
        {
            IsBackground = true,
            Name = "DadBoard.ActivateListener"
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
        }
        catch
        {
        }

        _activateEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
