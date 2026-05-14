using System;
using System.Collections.Concurrent;
using System.Text.Json;
using PglAttendance.Core.Models;

namespace PglAttendance.Service.Sync;

/// <summary>
/// Tiny event broadcaster. The desktop app connects via SSE (Server-Sent Events)
/// and gets pushed three event types — matching the NestJS WebSocket gateway:
///   - newRecord     (data: ParsedAttendanceVm JSON)
///   - syncUpdate    (data: { id, isSynced } JSON)
///   - statsUpdate   (data: {})
/// </summary>
public sealed class RealtimeBroadcaster
{
    public delegate void EventHandler(string eventName, string jsonData);
    private readonly ConcurrentDictionary<Guid, EventHandler> _subscribers = new();

    public Guid Subscribe(EventHandler handler)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = handler;
        return id;
    }

    public void Unsubscribe(Guid id) => _subscribers.TryRemove(id, out _);

    public void EmitNewRecord(ParsedAttendanceVm vm)
        => Emit("newRecord", JsonSerializer.Serialize(vm));

    public void EmitSyncUpdate(long id, bool isSynced)
        => Emit("syncUpdate", JsonSerializer.Serialize(new { id, isSynced }));

    public void EmitStatsUpdate()
        => Emit("statsUpdate", "{}");

    private void Emit(string evt, string data)
    {
        foreach (var h in _subscribers.Values)
        {
            try { h(evt, data); } catch { /* swallow per-subscriber errors */ }
        }
    }
}
