using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PglAttendance.Core;
using PglAttendance.Core.Data;
using PglAttendance.Core.Models;
using PglAttendance.Core.Sync;

namespace PglAttendance.Service.Sync;

/// <summary>
/// 100% identical behavior to NestJS app.service.ts:
///   - saveAttendance: line split, filter empty, parse, save, emit, batch vs single
///   - batch sort: status='0' asc by datetime, status='1' asc by datetime, others asc by id
///   - syncQueue: 1-second tick, FIFO, one at a time
///   - syncToHRMIS: POST raw body, expect literal "OK"
///   - retry: up to 10, then move to failedQueue with 10-minute cooldown
///   - failedRetry: 60-second tick, reset retryCount to 0 on re-queue
/// </summary>
public sealed class SyncEngine : IHostedService, IDisposable
{
    private readonly ILogger<SyncEngine> _logger;
    private readonly AttendanceRepository _repo;
    private readonly HrmisClient _hrmis;
    private readonly SettingsService _settings;
    private readonly RealtimeBroadcaster _realtime;

    private readonly ConcurrentQueue<QueueItem> _syncQueue = new();
    private readonly List<FailedItem> _failedQueue = new();
    private readonly object _failedLock = new();

    private readonly List<BatchItem> _batchCollector = new();
    private readonly object _batchLock = new();
    private Timer? _batchDebounce;

    private Timer? _syncTimer;
    private Timer? _failedTimer;

    private int _isProcessing;
    private int _isProcessingFailed;

    public SyncEngine(
        ILogger<SyncEngine> logger,
        AttendanceRepository repo,
        HrmisClient hrmis,
        SettingsService settings,
        RealtimeBroadcaster realtime)
    {
        _logger = logger;
        _repo = repo;
        _hrmis = hrmis;
        _settings = settings;
        _realtime = realtime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _repo.EnsureSchema();
        // 1-second sync queue tick (mirror setInterval(..., 1000))
        _syncTimer = new Timer(_ => _ = TickSyncQueueAsync(), null, 1000, 1000);
        // 60-second failed-retry tick (mirror setInterval(..., 60000))
        _failedTimer = new Timer(_ => _ = TickFailedRetryAsync(), null, 60000, 60000);
        _logger.LogInformation("Sync engine started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _syncTimer?.Dispose(); _syncTimer = null;
        _failedTimer?.Dispose(); _failedTimer = null;
        _batchDebounce?.Dispose(); _batchDebounce = null;
        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync(default).Wait();

    // ------------------------------------------------------------------
    // Mirror: saveAttendance(rawData: string)
    // ------------------------------------------------------------------
    public async Task<int> SaveAttendanceAsync(string rawData)
    {
        var lines = (rawData ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        var isBatch = lines.Count > 1;

        foreach (var line in lines)
        {
            _logger.LogInformation("Saving raw attendance: {Line}", line);
            var row = await _repo.InsertAsync(line);

            var vm = AttendanceParser.ToVm(row);
            // Emit live preview for anything that isn't device-operation-log noise.
            // The grid/stats no longer require tab-delimited rows, so the SSE
            // gate shouldn't either — otherwise non-tab test posts land in the
            // DB but never trigger a live refresh.
            if (!line.StartsWith("OPLOG", StringComparison.Ordinal))
            {
                // If the parser couldn't extract a UserId (no tab), surface
                // the raw line so the activity feed still shows something.
                if (string.IsNullOrEmpty(vm.UserId)) vm.UserId = line;
                _realtime.EmitNewRecord(vm);
                _realtime.EmitStatsUpdate();
            }

            if (isBatch)
            {
                lock (_batchLock)
                {
                    _batchCollector.Add(new BatchItem(row.Id, line, 0));
                }
            }
            else
            {
                // single line → call syncAllRecords like NestJS does
                await SyncAllRecordsAsync();
            }
        }

        if (isBatch)
        {
            _batchDebounce?.Dispose();
            _batchDebounce = new Timer(_ => ProcessBatch(), null, 2000, Timeout.Infinite);
        }

        return lines.Count;
    }

    // ------------------------------------------------------------------
    // Mirror: processBatch()
    // ------------------------------------------------------------------
    private void ProcessBatch()
    {
        List<BatchItem> snapshot;
        lock (_batchLock)
        {
            if (_batchCollector.Count == 0) return;
            snapshot = new List<BatchItem>(_batchCollector);
            _batchCollector.Clear();
        }

        // parse, then sort exactly like NestJS:
        //   status 0  -> by datetime ASC
        //   status 1  -> by datetime ASC
        //   others    -> by id ASC
        //   final order: [...others, ...status0, ...status1]
        var parsed = snapshot.Select(b =>
        {
            var p = AttendanceParser.Parse(b.RawData);
            return new { b.Id, b.RawData, b.RetryCount, Parsed = p };
        }).ToList();

        var status0 = parsed
            .Where(p => p.Parsed.Status == "0")
            .OrderBy(p => TryParseDt(p.Parsed.DateTime))
            .ToList();
        var status1 = parsed
            .Where(p => p.Parsed.Status == "1")
            .OrderBy(p => TryParseDt(p.Parsed.DateTime))
            .ToList();
        var others = parsed
            .Where(p => p.Parsed.Status != "0" && p.Parsed.Status != "1")
            .OrderBy(p => p.Id)
            .ToList();

        foreach (var p in others) _syncQueue.Enqueue(new QueueItem(p.Id, p.RawData, p.RetryCount));
        foreach (var p in status0) _syncQueue.Enqueue(new QueueItem(p.Id, p.RawData, p.RetryCount));
        foreach (var p in status1) _syncQueue.Enqueue(new QueueItem(p.Id, p.RawData, p.RetryCount));
    }

    private static DateTime TryParseDt(string s)
    {
        return DateTime.TryParse(s, out var dt) ? dt : DateTime.MinValue;
    }

    // ------------------------------------------------------------------
    // Mirror: startSyncProcess() setInterval(1s) -> pick first, sync
    // ------------------------------------------------------------------
    private async Task TickSyncQueueAsync()
    {
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0) return;
        try
        {
            if (!_syncQueue.TryDequeue(out var item)) return;
            await SyncToHrmisAsync(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessing, 0);
        }
    }

    private async Task SyncToHrmisAsync(QueueItem item)
    {
        var url = _settings.HrmisUrl;
        _logger.LogInformation("Syncing record ID {Id}, attempt {Attempt}", item.Id, item.RetryCount + 1);
        var result = await _hrmis.PostAsync(url, item.RawData);
        if (result.Ok)
        {
            await _repo.MarkSyncedAsync(item.Id);
            _logger.LogInformation("Successfully synced record ID {Id}", item.Id);
            _realtime.EmitSyncUpdate(item.Id, true);
            return;
        }
        // failure
        var errMsg = result.Error ?? $"HRMIS returned: {result.ResponseText}";
        _logger.LogError("Failed to sync record ID {Id}: {Err}", item.Id, errMsg);
        var nextRetry = item.RetryCount + 1;
        if (nextRetry < 10)
        {
            _syncQueue.Enqueue(item with { RetryCount = nextRetry });
        }
        else
        {
            lock (_failedLock)
            {
                _failedQueue.Add(new FailedItem(item.Id, item.RawData, nextRetry,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10L * 60L * 1000L));
            }
            try { await _repo.SetLastErrorAsync(item.Id, errMsg); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------
    // Mirror: startFailedRetryProcess() setInterval(60s)
    // ------------------------------------------------------------------
    private async Task TickFailedRetryAsync()
    {
        if (Interlocked.CompareExchange(ref _isProcessingFailed, 1, 0) != 0) return;
        try
        {
            FailedItem? eligible;
            lock (_failedLock)
            {
                if (_failedQueue.Count == 0) return;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                eligible = _failedQueue.FirstOrDefault(i => i.NextRetryMs <= now);
                if (eligible is null) return;
                _failedQueue.RemoveAll(i => i.Id == eligible.Id);
            }
            // NestJS resets retryCount = 0 on requeue
            _syncQueue.Enqueue(new QueueItem(eligible.Id, eligible.RawData, 0));
            await Task.CompletedTask;
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessingFailed, 0);
        }
    }

    // ------------------------------------------------------------------
    // Mirror: syncSelectedRecords(ids)
    // ------------------------------------------------------------------
    public sealed record SyncResult(bool Success, string Message);

    public async Task<SyncResult> SyncSelectedAsync(IEnumerable<long> ids)
    {
        var alreadySynced = await _repo.GetAlreadySyncedAmongAsync(ids);
        if (alreadySynced.Count > 0)
        {
            return new SyncResult(false,
                "One or more records you are trying to sync are already synced. Please refresh the page and try again.");
        }
        var rows = await _repo.GetUnsyncedByIdsAsync(ids);
        EnqueueWithBatchSort(rows);
        return new SyncResult(true, "Sync initiated for selected records");
    }

    // ------------------------------------------------------------------
    // Mirror: syncAllRecords()
    // ------------------------------------------------------------------
    public async Task<SyncResult> SyncAllRecordsAsync()
    {
        var rows = await _repo.GetUnsyncedForSyncAllAsync();
        if (rows.Count == 0) return new SyncResult(true, "No unsynced records found");
        // NestJS sync-all uses simple id-asc ordering (no status sort)
        foreach (var (id, raw) in rows)
            _syncQueue.Enqueue(new QueueItem(id, raw, 0));
        return new SyncResult(true, $"Sync initiated for {rows.Count} unsynced records");
    }

    private void EnqueueWithBatchSort(List<(long Id, string RawData)> rows)
    {
        var parsed = rows.Select(r => new
        {
            r.Id,
            r.RawData,
            Parsed = AttendanceParser.Parse(r.RawData),
        }).ToList();

        var status0 = parsed.Where(p => p.Parsed.Status == "0")
            .OrderBy(p => TryParseDt(p.Parsed.DateTime)).ToList();
        var status1 = parsed.Where(p => p.Parsed.Status == "1")
            .OrderBy(p => TryParseDt(p.Parsed.DateTime)).ToList();
        var others = parsed.Where(p => p.Parsed.Status != "0" && p.Parsed.Status != "1")
            .OrderBy(p => p.Id).ToList();

        foreach (var p in others) _syncQueue.Enqueue(new QueueItem(p.Id, p.RawData, 0));
        foreach (var p in status0) _syncQueue.Enqueue(new QueueItem(p.Id, p.RawData, 0));
        foreach (var p in status1) _syncQueue.Enqueue(new QueueItem(p.Id, p.RawData, 0));
    }

    private readonly record struct QueueItem(long Id, string RawData, int RetryCount);
    private readonly record struct BatchItem(long Id, string RawData, int RetryCount);
    private sealed record FailedItem(long Id, string RawData, int RetryCount, long NextRetryMs);
}
