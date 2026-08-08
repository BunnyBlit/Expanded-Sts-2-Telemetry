using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private static int _lastFailStatus; // HTTP status of last failure (0 = network error / last send OK)
    private static int _failStreak;     // consecutive failures of the same kind, for log throttling

    public static void Start(string serverUrl, string authToken)
    {
        if (_drainTask != null && !_drainTask.IsCompleted) return;
        // Set (or clear) the bearer token on the shared client each run. The HttpClient
        // is a long-lived singleton, so an explicit null clears a token from a prior run.
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(authToken)
            ? null
            : new AuthenticationHeaderValue("Bearer", authToken);
        _droppedCount = 0;
        _lastFailStatus = 0;
        _failStreak = 0;
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
            using HttpResponseMessage resp = await _http.PostAsync(serverUrl, content);
            if (resp.IsSuccessStatusCode)
            {
                NoteSuccess();
                return;
            }
            int code = (int)resp.StatusCode;
            string reason = code switch
            {
                401 or 403 => $"auth rejected (HTTP {code}) — check AuthToken/ServerUrl in expanded-telemetry.cfg",
                >= 500     => $"server error (HTTP {code})",
                _          => $"rejected (HTTP {code})",
            };
            NoteFailure(code, reason, lines.Count);
        }
        catch (Exception ex)
        {
            NoteFailure(0, $"network error: {ex.Message}", lines.Count);
        }
    }

    // Throttled failure logging. Logs the first failure of a given kind (HTTP status,
    // or 0 for a network exception), then only every 100th consecutive failure of that
    // same kind — so a persistently-down server or a bad token can't spam the log at the
    // 200ms drain cadence. A successful send resets the streak, so the next failure (or a
    // change to a different failure kind) logs immediately.
    private static void NoteSuccess()
    {
        _lastFailStatus = 0;
        _failStreak = 0;
    }

    private static void NoteFailure(int status, string reason, int dropped)
    {
        bool newKind = status != _lastFailStatus || _failStreak == 0;
        _failStreak = newKind ? 1 : _failStreak + 1;
        _lastFailStatus = status;
        if (newKind || _failStreak % 100 == 0)
        {
            string suffix = _failStreak > 1 ? $" (x{_failStreak})" : "";
            Log.Warn($"[expanded-telemetry] Remote send failed ({reason}) — {dropped} events dropped{suffix}");
        }
    }
}
