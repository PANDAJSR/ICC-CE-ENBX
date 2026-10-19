namespace ICC.CE.ENBX.Seewo.Services;

/// <summary>共享文件任务；后台最多占两个槽，前台总共可使用四个槽。</summary>
internal sealed class PriorityDownloadQueue : IDisposable
{
    private sealed class Work(Func<Task<string>> run, int priority)
    {
        public readonly Func<Task<string>> Run = run;
        public int Priority = priority;
        public bool Started;
        public readonly TaskCompletionSource<string> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Work> _work = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Work> _pending = new();
    private int _running;
    private int _background;
    private bool _disposed;

    public Task<string> GetAsync(string key, int priority, Func<Task<string>> run)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_work.TryGetValue(key, out var work))
            {
                work = new Work(run, priority);
                _work.Add(key, work);
                _pending.Add(work);
            }
            else if (!work.Started) work.Priority = Math.Min(work.Priority, priority);
            Pump();
            return work.Completion.Task;
        }
    }

    private void Pump()
    {
        while (!_disposed && _running < 4)
        {
            var next = _pending.Where(w => w.Priority < 2 || _background < 2)
                .OrderBy(w => w.Priority).FirstOrDefault();
            if (next == null) return;
            _pending.Remove(next);
            next.Started = true;
            var background = next.Priority >= 2;
            _running++;
            if (background) _background++;
            _ = Task.Run(async () =>
            {
                try { next.Completion.TrySetResult(await next.Run().ConfigureAwait(false)); }
                catch (OperationCanceledException) { next.Completion.TrySetCanceled(); }
                catch (Exception ex) { next.Completion.TrySetException(ex); }
                finally
                {
                    lock (_gate)
                    {
                        _running--;
                        if (background) _background--;
                        // 成功结果由磁盘缓存复用；失败任务允许下一次需求重试。
                        var key = _work.FirstOrDefault(pair => ReferenceEquals(pair.Value, next)).Key;
                        if (key != null) _work.Remove(key);
                        Pump();
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var work in _pending) work.Completion.TrySetCanceled();
            _pending.Clear();
        }
    }
}
