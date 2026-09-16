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
        if (queue is null)
        {
            // 창이 아직 없다(초기화 이전) — 마샬링할 UI 스레드 자체가 없으므로 그대로 실행해도 안전
            action();
            return;
        }

        if (queue.HasThreadAccess)
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
            // 디스패처가 이미 종료 중(창을 닫는 도중 등)이면 다른 스레드에서 그대로 실행하는
            // 게 오히려 RPC_E_WRONG_THREAD 로 인한 WinUI3 fail-fast 크래시를 유발할 수 있다.
            // 조용히 건너뛴다 — 이 시점엔 갱신할 UI 도 곧 사라진다.
            tcs.TrySetResult();
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
            tcs.TrySetResult();
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
        {
            tcs.TrySetException(new OperationCanceledException("UI 디스패처가 종료되어 실행할 수 없습니다."));
            return await tcs.Task.ConfigureAwait(false);
        }

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
