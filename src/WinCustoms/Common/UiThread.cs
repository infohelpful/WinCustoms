using Microsoft.UI.Dispatching;

namespace WinCustoms.Common;

/// <summary>
/// WinUI / WinRT 개체는 만든 스레드에서만 건드릴 수 있다.
/// ConfigureAwait(false) 이후 바인딩된 Observable 속성을 바꾸면 RPC_E_WRONG_THREAD 가 난다.
/// </summary>
internal static class UiThread
{
    private static DispatcherQueue? GetQueue()
        => App.Window?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();

    public static async Task InvokeAsync(Action action)
    {
        var queue = GetQueue();
        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            action();
            return;
        }

        await tcs.Task.ConfigureAwait(false);
    }

    public static async Task InvokeAsync(Func<Task> action)
    {
        var queue = GetQueue();
        if (queue is null || queue.HasThreadAccess)
        {
            await action().ConfigureAwait(true);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() => _ = RunOnQueueAsync(action, tcs)))
        {
            await action().ConfigureAwait(true);
            return;
        }

        await tcs.Task.ConfigureAwait(false);
    }

    public static async Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        var queue = GetQueue();
        if (queue is null || queue.HasThreadAccess)
            return await action().ConfigureAwait(true);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() => _ = RunOnQueueAsync(action, tcs)))
            return await action().ConfigureAwait(true);

        return await tcs.Task.ConfigureAwait(false);
    }

    private static async Task RunOnQueueAsync(Func<Task> action, TaskCompletionSource tcs)
    {
        try
        {
            await action().ConfigureAwait(true);
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private static async Task RunOnQueueAsync<T>(Func<Task<T>> action, TaskCompletionSource<T> tcs)
    {
        try
        {
            tcs.TrySetResult(await action().ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }
}
