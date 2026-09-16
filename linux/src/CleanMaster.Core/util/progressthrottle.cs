using System.Diagnostics;

namespace CleanMaster.Core.Util;

/// <summary>进度上报节流器：把高频更新压到固定间隔，避免 UI 线程被淹没。</summary>
public sealed class ProgressThrottle<T>
{
    private readonly IProgress<T>? _sink;
    private readonly int _intervalMs;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastPostMs;

    public ProgressThrottle(IProgress<T>? sink, int intervalMs = 150)
    {
        _sink = sink;
        _intervalMs = intervalMs;
    }

    /// <summary>到达间隔才上报；未到间隔直接丢弃（终局用 Flush）。</summary>
    public void Post(Func<T> make)
    {
        if (_sink == null) return;
        var now = _sw.ElapsedMilliseconds;
        if (now - _lastPostMs < _intervalMs) return;
        _lastPostMs = now;
        try { _sink.Report(make()); } catch { }
    }

    /// <summary>强制上报（用于完成/取消时）。</summary>
    public void Flush(Func<T> make)
    {
        if (_sink == null) return;
        _lastPostMs = _sw.ElapsedMilliseconds;
        try { _sink.Report(make()); } catch { }
    }
}
