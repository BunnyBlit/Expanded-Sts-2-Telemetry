using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Logging;

namespace ExpandedTelemetry;

// Fire-and-forget NDJSON sender. Game thread only enqueues strings (O(1), non-blocking).
// A background Task drains the queue every 200ms and POSTs batches to the configured URL.
// All network failures drop events silently after logging — gameplay is never affected.
internal static class RemoteSender
{
    private const int MaxQueueSize = 2000;
    private const int DrainIntervalMs = 200;
    private const int HttpTimeoutSeconds = 3;
    private const int BatchSize = 100;

    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds) };
    private static CancellationTokenSource? _cts;
    private static Task? _drainTask;
    private static int _droppedCount;

    public static void Start(string serverUrl)
    {
        if (_drainTask != null && !_drainTask.IsCompleted) return;
        _droppedCount = 0;
        _cts = new CancellationTokenSource();
        _drainTask = Task.Run(() => DrainLoop(serverUrl, _cts.Token));
    }

    // Called from the game thread. Non-blocking — enqueues or drops if full.
    public static void Enqueue(string json)
    {
        if (_queue.Count >= MaxQueueSize)
        {
            int dropped = Interlocked.Increment(ref _droppedCount);
            if (dropped == 1 || dropped % 100 == 0)
                Log.Warn($"[expanded-telemetry] Remote queue full — {dropped} events dropped");
            return;
        }
        _queue.Enqueue(json);
    }

    // Best-effort synchronous flush called at run end. Cancels the drain loop,
    // waits up to timeoutMs for the final batch to send, then resets state.
    public static void Flush(int timeoutMs = 3000)
    {
        _cts?.Cancel();
        try { _drainTask?.Wait(timeoutMs); } catch { }
        _cts = null;
        _drainTask = null;
        _droppedCount = 0;
    }

    private static async Task DrainLoop(string serverUrl, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(DrainIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
            await SendBatch(serverUrl);
        }
        await SendBatch(serverUrl); // final drain after cancellation
    }

    private static async Task SendBatch(string serverUrl)
    {
        if (_queue.IsEmpty) return;

        var lines = new List<string>(capacity: BatchSize);
        while (lines.Count < BatchSize && _queue.TryDequeue(out string? line))
            lines.Add(line);

        if (lines.Count == 0) return;

        try
        {
            var content = new StringContent(string.Join('\n', lines), Encoding.UTF8, "application/x-ndjson");
            await _http.PostAsync(serverUrl, content);
        }
        catch (Exception ex)
        {
            Log.Warn($"[expanded-telemetry] Remote send failed — {lines.Count} events dropped: {ex.Message}");
        }
    }
}
