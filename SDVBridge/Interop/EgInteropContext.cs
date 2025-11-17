using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SAS.Shared.AddIns;

namespace SDVBridge.Interop
{
    /// <summary>
    /// Captures the Enterprise Guide context (consumer + synchronization context) so background workers can marshal calls safely.
    /// </summary>
    internal sealed class EgInteropContext
    {
        private readonly SynchronizationContext _syncContext;
        private readonly ISASTaskConsumer _consumer;

        private EgInteropContext(ISASTaskConsumer consumer, SynchronizationContext syncContext)
        {
            _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
            _syncContext = syncContext ?? new WindowsFormsSynchronizationContext();
        }

        public ISASTaskConsumer Consumer => _consumer;

        public SynchronizationContext SyncContext => _syncContext;

        public static EgInteropContext Current { get; private set; }

        public static EgInteropContext EnsureInitialized(ISASTaskConsumer consumer)
        {
            if (consumer == null)
            {
                throw new ArgumentNullException(nameof(consumer));
            }

            if (Current == null)
            {
                var sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
                Current = new EgInteropContext(consumer, sync);
            }

            return Current;
        }

        public Task RunOnUiAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            var tcs = new TaskCompletionSource<object>();
            _syncContext.Post(_ =>
            {
                try
                {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, null);

            return tcs.Task;
        }

        public Task<T> RunOnUiAsync<T>(Func<T> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            var tcs = new TaskCompletionSource<T>();
            _syncContext.Post(_ =>
            {
                try
                {
                    var result = action();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, null);

            return tcs.Task;
        }
    }
}
