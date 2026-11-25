using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;

namespace Glassquid.Dock;

internal static class NativeTrayPopupAnimation
{
    /// <summary>Runs on the calling WPF dispatcher; false means the target stopped accepting frames.</summary>
    internal static Task<bool> RunAsync(
        TimeSpan duration, Func<double, bool> applyProgress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(applyProgress);
        if (token.IsCancellationRequested) return Task.FromCanceled<bool>(token);
        if (duration <= TimeSpan.Zero) return Task.FromResult(applyProgress(1));

        var dispatcher = Dispatcher.CurrentDispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return Task.FromCanceled<bool>(new CancellationToken(canceled: true));

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        var fallback = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = duration + TimeSpan.FromMilliseconds(200),
        };
        CancellationTokenRegistration cancellation = default;
        var stopped = false;

        bool Stop()
        {
            if (stopped) return false;
            stopped = true;
            CompositionTarget.Rendering -= OnRendering;
            fallback.Stop();
            fallback.Tick -= OnFallback;
            dispatcher.ShutdownStarted -= OnShutdown;
            cancellation.Dispose();
            stopwatch.Stop();
            return true;
        }

        void Cancel()
        {
            if (Stop()) completion.TrySetCanceled(token);
        }

        void Apply(double progress)
        {
            if (stopped) return;
            if (token.IsCancellationRequested)
            {
                Cancel();
                return;
            }

            try
            {
                var accepted = applyProgress(progress);
                if (token.IsCancellationRequested)
                {
                    Cancel();
                    return;
                }
                if ((!accepted || progress >= 1) && Stop())
                    completion.TrySetResult(accepted);
            }
            catch (Exception ex)
            {
                if (Stop()) completion.TrySetException(ex);
            }
        }

        void OnRendering(object? sender, EventArgs e) =>
            Apply(Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1));

        void OnFallback(object? sender, EventArgs e) => Apply(1);

        void OnShutdown(object? sender, EventArgs e)
        {
            if (Stop()) completion.TrySetCanceled(new CancellationToken(canceled: true));
        }

        CompositionTarget.Rendering += OnRendering;
        fallback.Tick += OnFallback;
        dispatcher.ShutdownStarted += OnShutdown;
        cancellation = token.Register(() =>
        {
            // Always queue cancellation so registration is assigned before cleanup,
            // even when Register observes an already-cancelled token synchronously.
            _ = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Cancel));
        });
        fallback.Start();
        Apply(0);
        return completion.Task;
    }
}
