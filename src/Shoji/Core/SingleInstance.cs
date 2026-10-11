using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Shoji.Core;

/// <summary>
/// One running copy at a time, plus a way to ask the running copy to exit (used by install, update and uninstall).
/// The mutex and event get an explicit "this user may use them" ACL: by default objects created by an elevated
/// (admin) copy can't be opened by a normal one, and updating while the admin autostart copy runs must still work.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\Shoji.SingleInstance";
    const string ExitEventName = @"Local\Shoji.Exit"; // also built from the app name in IsRunning / RequestExit

    readonly Mutex _mutex;
    EventWaitHandle? _exit;
    RegisteredWaitHandle? _waiter;

    SingleInstance(Mutex mutex) => _mutex = mutex;

    static SecurityIdentifier User => WindowsIdentity.GetCurrent().User!;

    /// <summary>Takes the single-instance lock, waiting up to <paramref name="wait"/>; null if another copy keeps running.</summary>
    public static SingleInstance? TryAcquire(TimeSpan wait)
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(User, MutexRights.FullControl, AccessControlType.Allow));
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                var mutex = MutexAcl.Create(false, MutexName, out _, security);
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; } // previous copy crashed; it's ours now
                if (owned)
                    return new SingleInstance(mutex);
                mutex.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                // Held by an older admin copy that created it without our ACL.
            }
            if (DateTime.UtcNow >= deadline)
                return null;
            Thread.Sleep(250);
        }
    }

    /// <summary>Runs <paramref name="onExitRequested"/> (on a pool thread) when another copy asks this one to exit.</summary>
    public void ListenForExit(Action onExitRequested)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(User, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        _exit = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ExitEventName, out _, security);
        _waiter = ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => onExitRequested(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <param name="app">Name prefix of the objects: "Shoji", or "WinDisplay" for the app's old name.</param>
    public static bool IsRunning(string app = "Shoji")
    {
        try
        {
            if (!Mutex.TryOpenExisting($@"Local\{app}.SingleInstance", out var m))
                return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Asks the running copy to exit and waits for it. True when no copy is running any more.</summary>
    public static bool RequestExit(TimeSpan wait, string app = "Shoji")
    {
        if (!IsRunning(app))
            return true;
        try
        {
            if (EventWaitHandle.TryOpenExisting($@"Local\{app}.Exit", out var ev))
            {
                ev.Set();
                ev.Dispose();
            }
        }
        catch (UnauthorizedAccessException)
        {
            // An older version without the exit signal; the caller asks the user to exit it from the tray.
        }

        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning(app))
                return true;
            Thread.Sleep(200);
        }
        return !IsRunning(app);
    }

    public void Dispose()
    {
        _waiter?.Unregister(null);
        _exit?.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
        _mutex.Dispose();
    }
}
