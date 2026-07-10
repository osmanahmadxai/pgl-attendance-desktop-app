using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PglAttendance.Core;
using PglAttendance.Core.Data;
using PglAttendance.Core.Sync;

namespace PglAttendance.Service.Sync;

/// <summary>
/// Reliable, exactly-once, oldest-first forwarder to HRMIS.
///
/// Guarantees:
///   1. Exactly-once: a record id can only be queued once (HashSet gate), and
///      the DB isSynced flag is re-checked immediately before every POST, so a
///      record is never sent twice — no matter how often "Sync all" is clicked
///      or how records arrive. Ingestion dedup (InsertOrGetAsync) additionally
///      collapses device re-uploads of the same punch into one row.
///   2. Oldest-first: the queue is a priority queue keyed by the punch
///      timestamp inside the record (tie-broken by row id), so records always
///      leave in chronological order regardless of arrival order. This matters
///      because HRMIS pairs check-outs against previously received check-ins.
///   3. Verbatim payload: rawData is POSTed exactly as the device sent it —
///      the timestamp is parsed only for ordering, never rewritten, and no
///      timezone conversion is applied anywhere.
///   4. Order-preserving retries: a transient failure (HRMIS down / network /
///      5xx) re-queues the SAME record and backs off — later records are never
///      sent ahead of it. A permanent rejection (HRMIS 4xx or non-OK body) is
///      recorded in lastError and skipped, so one bad record can't block the
///      queue forever.
///   5. Crash-safe: on service start, every unsynced row is re-queued, so
///      pending records survive restarts without user action.
/// </summary>
public sealed class SyncEngine : IHostedService, IDisposable
{
    private const int TickMs = 1000;
    private const int MaxBackoffSeconds = 300;

    /// <summary>
    /// A record that HRMIS actively answers with a transient-class error
    /// (5xx/408/429) this many times in a row is treated as poisoned and
    /// skipped (kept pending with lastError) so it can't block the queue
    /// forever. Network failures (no HTTP response at all) never count toward
    /// this — when HRMIS is unreachable, retrying the head forever is correct.
    /// </summary>
    private const int MaxResponseFailuresPerRecord = 10;

    private readonly ILogger<SyncEngine> _logger;
    private readonly AttendanceRepository _repo;
    private readonly HrmisClient _hrmis;
    private readonly SettingsService _settings;
    private readonly RealtimeBroadcaster _realtime;

    private readonly object _queueLock = new();
    private readonly PriorityQueue<QueueItem, PunchKey> _queue = new();
    private readonly HashSet<long> _queuedIds = new();
    // Rows already marked "not a valid attendance record" this session, so
    // repeated sync-all clicks don't rewrite the same lastError to the DB.
    private readonly HashSet<long> _invalidIds = new();
    private int _transientFailureStreak;
    private DateTime _nextAttemptUtc = DateTime.MinValue;

    private Timer? _syncTimer;
    private int _isProcessing;

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
        _syncTimer = new Timer(_ => _ = TickSyncQueueAsync(), null, TickMs, TickMs);

        // Crash/restart recovery: anything still pending goes back into the
        // queue (ordered by punch time like everything else). Runs in the
        // background so a large backlog can't delay service startup and the
        // device endpoint.
        _ = Task.Run(async () =>
        {
            try
            {
                var recovered = await SyncAllRecordsAsync();
                _logger.LogInformation("Startup recovery: {Message}", recovered.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup recovery failed — pending records can still be synced manually");
            }
        }, cancellationToken);

        _logger.LogInformation("Sync engine started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _syncTimer?.Dispose(); _syncTimer = null;
        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync(default).Wait();

    // ------------------------------------------------------------------
    // Ingestion: called for every device POST body.
    // ------------------------------------------------------------------
    public async Task<int> SaveAttendanceAsync(string rawData)
    {
        var lines = (rawData ?? "").Split('\n');
        var saved = 0;
        var anyNew = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            saved++;

            var (row, isNew) = await _repo.InsertOrGetAsync(line);
            if (isNew)
            {
                _logger.LogInformation("Saved raw attendance ID {Id}: {Line}", row.Id, line);

                if (!AttendanceParser.IsOplog(line))
                {
                    anyNew = true;
                    var vm = AttendanceParser.ToVm(row);
                    // If the parser couldn't extract a UserId (no tab), surface
                    // the raw line so the activity feed still shows something.
                    if (string.IsNullOrEmpty(vm.UserId)) vm.UserId = line;
                    _realtime.EmitNewRecord(vm);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Duplicate device upload for existing record ID {Id} (synced={Synced}) — not re-inserted: {Line}",
                    row.Id, row.IsSynced, line);
            }

            // Sync immediately when possible; if HRMIS is down the record just
            // stays pending in the queue and retries in order.
            if (!row.IsSynced)
                await TryEnqueueAsync(row.Id, row.RawData);
        }

        // One stats broadcast per POST body, not one per line — a large batch
        // would otherwise trigger hundreds of client refreshes.
        if (anyNew) _realtime.EmitStatsUpdate();

        return saved;
    }

    // ------------------------------------------------------------------
    // Queue admission — the single gate every record passes through.
    // ------------------------------------------------------------------
    private async Task<bool> TryEnqueueAsync(long id, string rawData)
    {
        if (AttendanceParser.IsOplog(rawData))
            return false;

        // Only records HRMIS would actually store go on the wire. Anything
        // else HRMIS either swallows with a fake "OK" (silent data loss) or
        // rejects — so we keep such rows local and mark why they won't sync.
        if (!AttendanceParser.IsValidAttendanceRecord(rawData))
        {
            bool firstTime;
            lock (_queueLock) firstTime = _invalidIds.Add(id);
            if (firstTime)
            {
                _logger.LogWarning("Record ID {Id} is not a valid attendance record; excluded from sync: {Raw}", id, rawData);
                try { await _repo.SetLastErrorAsync(id, "Not a valid attendance record — excluded from sync"); }
                catch { /* best effort */ }
            }
            return false;
        }

        var key = KeyFor(id, rawData);
        lock (_queueLock)
        {
            if (!_queuedIds.Add(id)) return false; // already queued or in flight
            _queue.Enqueue(new QueueItem(id, rawData, 0), key);
        }
        return true;
    }

    private static PunchKey KeyFor(long id, string rawData)
    {
        // Parsed only to decide ordering; the payload itself is never touched.
        // A record whose timestamp fields pass the format gate but aren't a
        // real calendar date/time sorts LAST (MaxValue), so a freak record can
        // never jump ahead of legitimate punches.
        var punch = AttendanceParser.TryGetPunchTimestamp(rawData, out var dt) ? dt : DateTime.MaxValue;
        return new PunchKey(punch, id);
    }

    // ------------------------------------------------------------------
    // 1-second tick: send exactly one record, oldest punch first.
    // ------------------------------------------------------------------
    private async Task TickSyncQueueAsync()
    {
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0) return;
        try
        {
            QueueItem item;
            lock (_queueLock)
            {
                if (DateTime.UtcNow < _nextAttemptUtc) return; // backing off after transient failure
                if (!_queue.TryDequeue(out item, out _)) return;
                // item.Id intentionally stays in _queuedIds while in flight so
                // a concurrent "Sync all" can't queue it a second time.
            }
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
        try
        {
            // Exactly-once gate: whatever queued this item, the DB is the
            // source of truth right before the wire.
            var synced = await _repo.IsSyncedAsync(item.Id);
            if (synced is null)
            {
                _logger.LogInformation("Record ID {Id} no longer exists (deleted) — dropped from queue", item.Id);
                ReleaseId(item.Id);
                return;
            }
            if (synced == true)
            {
                _logger.LogInformation("Record ID {Id} already synced — dropped from queue", item.Id);
                ReleaseId(item.Id);
                return;
            }

            var result = await _hrmis.PostAsync(_settings.HrmisUrl, item.RawData);

            if (result.Ok)
            {
                // If we crash between the POST and this write, the record is
                // re-sent on recovery — HRMIS dedupes raw punches on
                // (userId, datetime, status), so the re-send is a no-op there.
                await _repo.MarkSyncedAsync(item.Id);
                ReleaseId(item.Id);
                ResetBackoff();
                _logger.LogInformation("Successfully synced record ID {Id}", item.Id);
                _realtime.EmitSyncUpdate(item.Id, true);
                _realtime.EmitStatsUpdate();
                return;
            }

            if (result.IsTransient)
            {
                // Only failures where HRMIS actually answered count toward the
                // per-record poison cap; if HRMIS is unreachable, nothing is
                // wrong with the record and it must keep its place in line.
                var responseFailures = result.HasResponse ? item.ResponseFailures + 1 : item.ResponseFailures;
                if (responseFailures >= MaxResponseFailuresPerRecord)
                {
                    var poisonMsg = $"Skipped after {responseFailures} failed attempts — {result.Error}";
                    _logger.LogError(
                        "Record ID {Id} keeps failing with a server response — skipping so the queue can drain: {Err}",
                        item.Id, result.Error);
                    try { await _repo.SetLastErrorAsync(item.Id, poisonMsg); } catch { /* best effort */ }
                    ReleaseId(item.Id);
                    ResetBackoff();
                    _realtime.EmitSyncUpdate(item.Id, false);
                    return;
                }

                // Keep this record at the head so chronological order is
                // preserved, and back off.
                RequeueInFlight(item with { ResponseFailures = responseFailures });
                var delay = ApplyBackoff();
                _logger.LogWarning(
                    "Transient failure syncing record ID {Id} ({Err}) — will retry in {Delay}s, queue order preserved",
                    item.Id, result.Error, delay);
                return;
            }

            // Permanent rejection — HRMIS will never accept this record.
            var errMsg = result.Error ?? $"HRMIS returned: {result.ResponseText}";
            _logger.LogError("HRMIS rejected record ID {Id}: {Err}", item.Id, errMsg);
            try { await _repo.SetLastErrorAsync(item.Id, errMsg); } catch { /* best effort */ }
            ReleaseId(item.Id);
            ResetBackoff();
            _realtime.EmitSyncUpdate(item.Id, false);
        }
        catch (Exception ex)
        {
            // Unexpected local fault (e.g. DB briefly locked) — treat as
            // transient so the record is never lost.
            _logger.LogError(ex, "Unexpected error syncing record ID {Id} — will retry", item.Id);
            RequeueInFlight(item);
            ApplyBackoff();
        }
    }

    /// <summary>
    /// Puts an in-flight item back. Re-adds the id to _queuedIds rather than
    /// assuming it's still there — ClearQueue may have run while the item was
    /// on the wire, and queue + id-set must never diverge.
    /// </summary>
    private void RequeueInFlight(QueueItem item)
    {
        lock (_queueLock)
        {
            _queuedIds.Add(item.Id);
            _queue.Enqueue(item, KeyFor(item.Id, item.RawData));
        }
    }

    private void ReleaseId(long id)
    {
        lock (_queueLock) _queuedIds.Remove(id);
    }

    private int ApplyBackoff()
    {
        lock (_queueLock)
        {
            _transientFailureStreak++;
            var delay = Math.Min(MaxBackoffSeconds, 1 << Math.Min(_transientFailureStreak, 8));
            _nextAttemptUtc = DateTime.UtcNow.AddSeconds(delay);
            return delay;
        }
    }

    private void ResetBackoff()
    {
        lock (_queueLock)
        {
            _transientFailureStreak = 0;
            _nextAttemptUtc = DateTime.MinValue;
        }
    }

    // ------------------------------------------------------------------
    // Manual sync entry points (UI buttons / HTTP endpoints)
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
        var queued = 0;
        foreach (var (id, raw) in rows)
            if (await TryEnqueueAsync(id, raw)) queued++;
        return new SyncResult(true, $"Sync initiated for {queued} record(s)");
    }

    public async Task<SyncResult> SyncAllRecordsAsync()
    {
        var rows = await _repo.GetUnsyncedForSyncAllAsync();
        if (rows.Count == 0) return new SyncResult(true, "No unsynced records found");
        var queued = 0;
        foreach (var (id, raw) in rows)
            if (await TryEnqueueAsync(id, raw)) queued++;
        return new SyncResult(true,
            $"Sync initiated: {queued} record(s) queued, {rows.Count - queued} already queued or excluded");
    }

    /// <summary>
    /// Empties the queue. Used before wiping the local database so no stale
    /// entry can reference a deleted row (the send-time IsSyncedAsync check is
    /// the backstop for any item already in flight).
    /// </summary>
    public void ClearQueue()
    {
        lock (_queueLock)
        {
            _queue.Clear();
            _queuedIds.Clear();
            _invalidIds.Clear();
            _transientFailureStreak = 0;
            _nextAttemptUtc = DateTime.MinValue;
        }
        _logger.LogInformation("Sync queue cleared");
    }

    /// <summary>ResponseFailures counts consecutive answered-error attempts (poison-record cap).</summary>
    private readonly record struct QueueItem(long Id, string RawData, int ResponseFailures);

    /// <summary>Chronological order: punch timestamp, then row id.</summary>
    private readonly record struct PunchKey(DateTime Punch, long Id) : IComparable<PunchKey>
    {
        public int CompareTo(PunchKey other)
        {
            var c = Punch.CompareTo(other.Punch);
            return c != 0 ? c : Id.CompareTo(other.Id);
        }
    }
}
