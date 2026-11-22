using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace LinkPilot.Link
{
    /// <summary>
    /// The system-wide mutex that Corsair-aware tools (SignalRGB, FanControl's CorsairLink plugin, etc.)
    /// hold while they talk to Corsair hubs. Holding it around every transaction keeps our
    /// request/response pairs from getting interleaved with SignalRGB's lighting traffic.
    /// </summary>
    public sealed class CorsairGuard : IDisposable
    {
        public const string SharedMutexName = @"Global\CorsairLinkReadWriteGuardMutex";

        private readonly Mutex _mutex;
        public bool IsShared { get; }
        public string Description { get; }

        public CorsairGuard()
        {
            try
            {
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
                _mutex = new Mutex(false, SharedMutexName, out _, security);
                IsShared = true;
                Description = "Sharing the Corsair device lock with other apps";
            }
            catch (UnauthorizedAccessException)
            {
                try
                {
                    // Someone (often an elevated service) created it with a stricter ACL: open, don't create.
                    _mutex = Mutex.OpenExisting(SharedMutexName, MutexRights.Synchronize | MutexRights.Modify);
                    IsShared = true;
                    Description = "Sharing the Corsair device lock with other apps";
                }
                catch (Exception)
                {
                    _mutex = new Mutex(false);
                    IsShared = false;
                    Description = "Couldn't access the shared Corsair lock (try running as admin if SignalRGB acts up)";
                }
            }
        }

        /// <summary>Waits for exclusive access. Returns null if another app hogged it past the timeout.</summary>
        public IDisposable? TryAcquire(int timeoutMs = 2500)
        {
            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(timeoutMs);
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // previous owner crashed; we own it now
            }
            return acquired ? new Releaser(_mutex) : null;
        }

        private sealed class Releaser : IDisposable
        {
            private Mutex? _m;
            public Releaser(Mutex m) { _m = m; }
            public void Dispose()
            {
                var m = Interlocked.Exchange(ref _m, null);
                m?.ReleaseMutex();
            }
        }

        public void Dispose() => _mutex.Dispose();
    }
}
