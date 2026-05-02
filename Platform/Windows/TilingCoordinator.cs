using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TinyBoss.Platform.Windows;

/// <summary>
/// Slot numbering for each grid layout:
/// 2-pane: [0=Left, 1=Right]
/// 4-pane: [0=TopLeft, 1=TopRight, 2=BottomLeft, 3=BottomRight]
/// 6-pane: [0=TopLeft, 1=TopCenter, 2=TopRight, 3=BottomLeft, 4=BottomCenter, 5=BottomRight]
/// </summary>
public sealed record TileSlot(nint Hwnd, int ProcessId, string? SessionId, string? Alias = null);

public sealed record TileLocation(string DeviceName, nint MonitorHandle, int Slot, TileSlot TileSlot);

public sealed record MonitorGridSnapshot(
    string DeviceName,
    nint MonitorHandle,
    IReadOnlyDictionary<int, TileSlot> Slots,
    int GridSize);

public sealed record AliasReapplyResult(int Checked, int Renamed, int MissingAlias, int Failed);

internal sealed class MonitorGridState
{
    public required string DeviceName { get; init; }
    public nint MonitorHandle { get; set; }
    public Dictionary<int, TileSlot> Slots { get; } = new();
    public int OccupiedCount => Slots.Count;
    public int GridSize => TilingCoordinator.ComputeGridSize(OccupiedCount);
    public int GridSizeForNextWindow => TilingCoordinator.ComputeGridSize(OccupiedCount + 1);
}

internal sealed record DetachedTile(string DeviceName, nint MonitorHandle, int Slot, TileSlot TileSlot);
internal sealed record MonitorHandleInfo(string DeviceName, nint MonitorHandle);
internal sealed record VisibleTerminalInfo(nint Hwnd, string DeviceName, int SpatialOrder);

/// <summary>
/// Coordinates tiling state and Win32 window positioning across monitor grids.
/// All mutations are serialized through a lock to prevent race conditions.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TilingCoordinator : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, MonitorGridState> _grids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, DetachedTile> _detachedTiles = new();
    private readonly Dictionary<nint, Process> _processWaits = new();
    private readonly Dictionary<nint, Timer> _aliasTitleReapplyTimers = new();
    private readonly LiveWindowAliasMemory _aliasMemory = new();
    private readonly ILogger<TilingCoordinator> _logger;
    private WinEventDelegate? _nameChangeDelegate;
    private nint _nameChangeHook;
    private string? _activeMonitorDevice;
    private nint _activeMonitor;
    private bool _disposed;
    private Timer? _reflowDebounce;
    private nint _pendingReflowMonitor;
    private bool _dragInProgress;
    private bool _reflowPending;
    private const int REFLOW_DEBOUNCE_MS = 100;
    private const int ALIAS_TITLE_ENFORCE_INTERVAL_MS = 100;

    public event Action? SlotsChanged;

    /// <summary>Grid layout for 6-pane: "2x3" (2 rows × 3 cols) or "3x2" (3 rows × 2 cols).</summary>
    public string Layout { get; set; } = "2x3";

    /// <summary>The most recently active monitor for compatibility callers.</summary>
    public nint ActiveMonitor => _activeMonitor;

    public TilingCoordinator(ILogger<TilingCoordinator> logger)
    {
        _logger = logger;
    }

    public int GridSize
    {
        get
        {
            lock (_lock)
                return GetActiveGridLocked()?.GridSize ?? 1;
        }
    }

    public int GridSizeForNextWindow
    {
        get
        {
            lock (_lock)
                return GetActiveGridLocked()?.GridSizeForNextWindow ?? 1;
        }
    }

    public int OccupiedCount
    {
        get
        {
            lock (_lock)
                return GetActiveGridLocked()?.OccupiedCount ?? 0;
        }
    }

    public static int ComputeGridSize(int occupiedCount) => occupiedCount switch
    {
        <= 1 => 1,
        2 => 2,
        3 or 4 => 4,
        _ => 6,
    };

    // ── Slot queries ────────────────────────────────────────────────────────

    public IReadOnlyDictionary<int, TileSlot> GetSnapshot()
    {
        lock (_lock)
            return SnapshotForGridLocked(GetActiveGridLocked()).Slots;
    }

    public IReadOnlyDictionary<int, TileSlot> GetSnapshot(nint monitorHandle)
    {
        lock (_lock)
            return SnapshotForGridLocked(GetOrCreateGridLocked(monitorHandle)).Slots;
    }

    public IReadOnlyList<MonitorGridSnapshot> GetAllSnapshots()
    {
        lock (_lock)
        {
            PruneDeadWindowsLocked();
            return _grids.Values
                .Where(g => g.Slots.Count > 0)
                .OrderBy(g => g.DeviceName, StringComparer.OrdinalIgnoreCase)
                .Select(SnapshotForGridLocked)
                .ToArray();
        }
    }

    public int GetGridSize(nint monitorHandle)
    {
        lock (_lock)
            return GetOrCreateGridLocked(monitorHandle).GridSize;
    }

    public int GetGridSizeForNextWindow(nint monitorHandle)
    {
        lock (_lock)
            return GetOrCreateGridLocked(monitorHandle).GridSizeForNextWindow;
    }

    public int GetOccupiedCount(nint monitorHandle)
    {
        lock (_lock)
            return GetOrCreateGridLocked(monitorHandle).OccupiedCount;
    }

    public bool IsSlotOccupied(int slot)
    {
        lock (_lock)
            return GetActiveGridLocked()?.Slots.ContainsKey(slot) == true;
    }

    public bool IsSlotOccupied(nint monitorHandle, int slot)
    {
        lock (_lock)
            return GetOrCreateGridLocked(monitorHandle).Slots.ContainsKey(slot);
    }

    public int FindSlotForHwnd(nint hwnd)
    {
        lock (_lock)
            return FindLocationForHwndLocked(hwnd)?.Slot ?? -1;
    }

    public TileLocation? FindLocationForHwnd(nint hwnd)
    {
        lock (_lock)
            return FindLocationForHwndLocked(hwnd);
    }

    // ── Core mutations ──────────────────────────────────────────────────────

    public bool AssignToSlot(int slot, nint hwnd, string? sessionId = null)
    {
        var monitor = GetMonitorAtCursor();
        return AssignToSlot(slot, hwnd, monitor, sessionId);
    }

    public bool AssignToSlot(int slot, nint hwnd, nint monitorHandle, string? sessionId = null)
    {
        lock (_lock)
        {
            if (!IsWindow(hwnd))
            {
                _logger.LogWarning("KH: Cannot tile invalid HWND {Hwnd}", hwnd);
                return false;
            }

            var grid = GetOrCreateGridLocked(monitorHandle);

            if (grid.Slots.Count >= PageMovePlanner.MaxWindowsPerGrid &&
                !grid.Slots.ContainsKey(slot) &&
                !grid.Slots.Any(kv => kv.Value.Hwnd == hwnd))
            {
                return false;
            }

            RemoveHwndLocked(hwnd, clearAlias: false);
            _detachedTiles.Remove(hwnd);

            if (grid.Slots.ContainsKey(slot))
                RemoveSlotLocked(grid, slot, clearAlias: false);

            GetWindowThreadProcessId(hwnd, out int pid);
            var alias = _aliasMemory.Get(hwnd);
            var tileSlot = new TileSlot(hwnd, pid, sessionId, alias);
            grid.Slots[slot] = tileSlot;
            ApplyAliasTitleWithRetriesLocked(tileSlot);
            RegisterProcessExitLocked(hwnd, pid);
            SetActiveGridLocked(grid);

            _logger.LogInformation(
                "KH: Tiled HWND {Hwnd} (PID {Pid}) to {Device} slot {Slot}",
                hwnd, pid, grid.DeviceName, slot);
        }

        SlotsChanged?.Invoke();
        return true;
    }

    public bool SwapDetachedWindowWithSlot(
        nint draggedHwnd,
        nint targetMonitorHandle,
        int targetSlot,
        TileLocation sourceLocation,
        string? draggedSessionId = null)
    {
        nint sourceMonitor;
        nint targetMonitor;

        lock (_lock)
        {
            if (!IsWindow(draggedHwnd))
            {
                _logger.LogWarning("KH: Cannot swap invalid HWND {Hwnd}", draggedHwnd);
                return false;
            }

            var targetGrid = GetOrCreateGridLocked(targetMonitorHandle);
            if (!targetGrid.Slots.TryGetValue(targetSlot, out var targetTile))
                return false;

            if (targetTile.Hwnd == draggedHwnd)
                return false;

            var sourceGrid = GetOrCreateGridLocked(sourceLocation.MonitorHandle);
            var sourceSlot = sourceLocation.Slot;
            if (sourceGrid.Slots.ContainsKey(sourceSlot))
            {
                sourceSlot = FindNextEmptySlotLocked(sourceGrid, PageMovePlanner.MaxWindowsPerGrid);
                if (sourceSlot < 0)
                    return false;
            }

            RemoveHwndLocked(draggedHwnd, clearAlias: false);
            _detachedTiles.Remove(draggedHwnd);
            _detachedTiles.Remove(targetTile.Hwnd);

            GetWindowThreadProcessId(draggedHwnd, out int draggedPid);
            var draggedAlias = sourceLocation.TileSlot.Alias ?? _aliasMemory.Get(draggedHwnd);
            var displacedAlias = targetTile.Alias ?? _aliasMemory.Get(targetTile.Hwnd);

            targetGrid.Slots.Remove(targetSlot);
            sourceGrid.Slots[sourceSlot] = targetTile with { Alias = displacedAlias };
            var draggedTile = new TileSlot(
                draggedHwnd,
                draggedPid,
                draggedSessionId ?? sourceLocation.TileSlot.SessionId,
                draggedAlias);
            targetGrid.Slots[targetSlot] = draggedTile;
            ApplyAliasTitleWithRetriesLocked(sourceGrid.Slots[sourceSlot]);
            ApplyAliasTitleWithRetriesLocked(draggedTile);

            RegisterProcessExitLocked(targetTile.Hwnd, targetTile.ProcessId);
            RegisterProcessExitLocked(draggedHwnd, draggedPid);
            SetActiveGridLocked(targetGrid);
            sourceMonitor = sourceGrid.MonitorHandle;
            targetMonitor = targetGrid.MonitorHandle;

            DiagLog(
                $"SLOT_SWAPPED source={sourceGrid.DeviceName}:{sourceSlot} target={targetGrid.DeviceName}:{targetSlot} dragged=0x{draggedHwnd:X} displaced=0x{targetTile.Hwnd:X}");
        }

        SlotsChanged?.Invoke();
        if (sourceMonitor != nint.Zero)
            PositionAll(sourceMonitor);
        if (targetMonitor != nint.Zero && targetMonitor != sourceMonitor)
            PositionAll(targetMonitor);
        return true;
    }

    public bool RemoveWindow(nint hwnd)
    {
        nint reflowMonitor = nint.Zero;
        bool removed;
        lock (_lock)
        {
            var location = FindLocationForHwndLocked(hwnd);
            if (location is null || !_grids.TryGetValue(location.DeviceName, out var grid))
            {
                removed = false;
            }
            else
            {
                var alias = location.TileSlot.Alias ?? _aliasMemory.Get(hwnd);
                _detachedTiles[hwnd] = new DetachedTile(
                    location.DeviceName,
                    location.MonitorHandle,
                    location.Slot,
                    location.TileSlot with { Alias = alias });
                removed = RemoveSlotLocked(grid, location.Slot, clearAlias: false);
            }
            reflowMonitor = location?.MonitorHandle ?? nint.Zero;
        }

        if (removed)
        {
            SlotsChanged?.Invoke();
            if (reflowMonitor != nint.Zero)
                ScheduleReflow(reflowMonitor);
        }

        return removed;
    }

    public bool RestoreDetachedWindow(nint hwnd)
    {
        nint monitor = nint.Zero;
        bool restored = false;
        lock (_lock)
        {
            PruneDetachedTilesLocked();
            restored = RestoreDetachedWindowLocked(hwnd, out monitor);
        }

        if (restored)
        {
            SlotsChanged?.Invoke();
            if (monitor != nint.Zero)
                PositionAll(monitor);
        }

        return restored;
    }

    public int ReactivateKnownWindows(nint monitorHandle)
    {
        int changed = 0;
        lock (_lock)
        {
            var grid = GetOrCreateGridLocked(monitorHandle);
            PruneDeadWindowsLocked(grid);
            PruneDetachedTilesLocked();
            changed += ReattachDetachedTilesLocked(grid);

            if (grid.Slots.Count == 0)
                changed += RecoverVisibleTerminalWindowsLocked(grid);

            if (grid.Slots.Count > 0)
                SetActiveGridLocked(grid);
        }

        if (changed > 0)
            SlotsChanged?.Invoke();
        return changed;
    }

    public int CollectVisibleTerminals(IReadOnlyCollection<string>? enabledDeviceNames, string trigger)
    {
        var monitors = EnumerateMonitorHandles(enabledDeviceNames);
        var visible = EnumerateVisibleTerminalWindows(monitors);
        if (monitors.Count == 0)
        {
            DiagLog($"TERMINAL_COLLECT trigger={trigger} enabled=0 visible={visible.Count} assigned=0 loose={visible.Count}");
            return 0;
        }

        var monitorOrder = monitors.Select(m => m.DeviceName).ToArray();
        var monitorByDevice = monitors.ToDictionary(m => m.DeviceName, StringComparer.OrdinalIgnoreCase);
        var tileByHwnd = new Dictionary<nint, TileSlot>();
        var candidates = new List<TerminalCollectCandidate<nint>>();
        TerminalCollectPlan<nint> plan;

        lock (_lock)
        {
            PruneDeadWindowsLocked();
            PruneDetachedTilesLocked();

            foreach (var terminal in visible)
            {
                var location = FindLocationForHwndLocked(terminal.Hwnd);
                var existingSlot = location is not null &&
                    string.Equals(location.DeviceName, terminal.DeviceName, StringComparison.OrdinalIgnoreCase)
                        ? location.Slot
                        : (int?)null;
                candidates.Add(new TerminalCollectCandidate<nint>(
                    terminal.Hwnd,
                    terminal.DeviceName,
                    existingSlot,
                    terminal.SpatialOrder));

                GetWindowThreadProcessId(terminal.Hwnd, out int pid);
                var alias = location?.TileSlot.Alias ?? _aliasMemory.Get(terminal.Hwnd);
                tileByHwnd[terminal.Hwnd] = new TileSlot(
                    terminal.Hwnd,
                    pid,
                    location?.TileSlot.SessionId,
                    alias);
            }

            plan = TerminalCollectPlanner.Plan(monitorOrder, candidates);

            foreach (var grid in _grids.Values)
            {
                foreach (var slot in grid.Slots.Keys.ToList())
                {
                    RemoveSlotLocked(grid, slot, clearAlias: false);
                }
            }

            foreach (var monitor in monitors)
            {
                var grid = GetOrCreateGridLocked(monitor.MonitorHandle);
                grid.Slots.Clear();

                if (!plan.Assigned.TryGetValue(monitor.DeviceName, out var assigned))
                    continue;

                var fillOrder = GetFillOrder(ComputeGridSize(assigned.Count), Layout);
                for (int i = 0; i < assigned.Count && i < fillOrder.Count; i++)
                {
                    var hwnd = assigned[i];
                    if (!tileByHwnd.TryGetValue(hwnd, out var tileSlot) || !IsWindow(hwnd))
                        continue;

                    var reassigned = tileSlot with { Alias = tileSlot.Alias ?? _aliasMemory.Get(hwnd) };
                    grid.Slots[fillOrder[i]] = reassigned;
                    ApplyAliasTitleWithRetriesLocked(reassigned);
                    RegisterProcessExitLocked(hwnd, tileSlot.ProcessId);
                    _detachedTiles.Remove(hwnd);
                }

                if (grid.Slots.Count > 0)
                    SetActiveGridLocked(grid);
            }
        }

        foreach (var monitor in monitors)
            PositionAll(monitor.MonitorHandle);

        var assignedCount = plan.Assigned.Values.Sum(v => v.Count);
        var perMonitor = string.Join(" ", plan.Assigned.Select(kv => $"{kv.Key}={kv.Value.Count}"));
        DiagLog($"TERMINAL_COLLECT trigger={trigger} enabled={monitors.Count} visible={visible.Count} assigned={assignedCount} loose={plan.Unmanaged.Count} {perMonitor}");

        SlotsChanged?.Invoke();
        return assignedCount;
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            foreach (var proc in _processWaits.Values)
                try { proc.Dispose(); } catch { }
            _processWaits.Clear();
            _grids.Clear();
            _activeMonitor = nint.Zero;
            _activeMonitorDevice = null;
        }
        SlotsChanged?.Invoke();
    }

    public void DetachAll()
    {
        lock (_lock)
        {
            DiagLog($"DETACH_ALL grids={_grids.Count} processWaits={_processWaits.Count}");
            foreach (var grid in _grids.Values)
                grid.Slots.Clear();
            _activeMonitor = nint.Zero;
            _activeMonitorDevice = null;
        }
        SlotsChanged?.Invoke();
    }

    public bool MovePage(string sourceDevice, string targetDevice)
    {
        nint sourceMonitor;
        nint targetMonitor;
        int moved;

        lock (_lock)
        {
            if (string.Equals(sourceDevice, targetDevice, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!_grids.TryGetValue(sourceDevice, out var source) ||
                !_grids.TryGetValue(targetDevice, out var target))
            {
                return false;
            }

            PruneDeadWindowsLocked();
            CompactGridLocked(source);
            CompactGridLocked(target);

            var sourceOrdered = GetOrderedTileSlots(source).ToArray();
            var targetOrdered = GetOrderedTileSlots(target).ToArray();
            var plan = PageMovePlanner.Plan(targetOrdered, sourceOrdered);
            moved = plan.Moved.Count;
            if (moved == 0)
                return false;

            RewriteGridSlotsLocked(target, plan.Target);
            RewriteGridSlotsLocked(source, plan.Source);
            sourceMonitor = source.MonitorHandle;
            targetMonitor = target.MonitorHandle;
            SetActiveGridLocked(target);
        }

        if (sourceMonitor != nint.Zero)
            PositionAll(sourceMonitor);
        if (targetMonitor != nint.Zero)
            PositionAll(targetMonitor);

        DiagLog($"PAGE_MOVE source={sourceDevice} target={targetDevice} moved={moved}");
        SlotsChanged?.Invoke();
        return true;
    }

    public bool PruneDeadWindows()
    {
        var pruned = false;
        lock (_lock)
        {
            pruned = PruneDeadWindowsLocked();
        }

        if (pruned)
            SlotsChanged?.Invoke();

        return pruned;
    }

    // ── Drag-aware reflow ───────────────────────────────────────────────────

    public void OnDragStarted() => _dragInProgress = true;

    public void OnDragEnded(nint monitorHandle, bool applyPendingReflow = true)
    {
        _dragInProgress = false;
        if (_reflowPending)
        {
            _reflowPending = false;
            if (applyPendingReflow)
                ScheduleReflow(_pendingReflowMonitor != nint.Zero ? _pendingReflowMonitor : monitorHandle);
            _pendingReflowMonitor = nint.Zero;
        }
    }

    public void ScheduleReflow(nint monitorHandle)
    {
        if (monitorHandle == nint.Zero)
            return;

        if (_dragInProgress)
        {
            _reflowPending = true;
            _pendingReflowMonitor = monitorHandle;
            return;
        }

        _reflowDebounce?.Dispose();
        _reflowDebounce = new Timer(_ => Rebalance(monitorHandle), null, REFLOW_DEBOUNCE_MS, Timeout.Infinite);
    }

    // ── Rename / alias memory ───────────────────────────────────────────────

    public bool RenameSlot(int slot, string alias)
    {
        lock (_lock)
        {
            var grid = GetActiveGridLocked();
            return grid is not null && RenameSlotLocked(grid, slot, alias);
        }
    }

    public bool RenameSlot(nint monitorHandle, int slot, string alias)
    {
        lock (_lock)
            return RenameSlotLocked(GetOrCreateGridLocked(monitorHandle), slot, alias);
    }

    public bool RenameWindow(nint hwnd, string alias)
    {
        lock (_lock)
        {
            var location = FindLocationForHwndLocked(hwnd);
            if (location is null)
                return false;

            return RenameSlotLocked(GetOrCreateGridLocked(location.MonitorHandle), location.Slot, alias);
        }
    }

    public bool RememberWindowAlias(nint hwnd, string? alias, bool applyNow = true)
    {
        lock (_lock)
        {
            if (hwnd == nint.Zero || !IsWindow(hwnd))
                return false;

            var normalizedAlias = NormalizeAlias(alias);
            _aliasMemory.Set(hwnd, normalizedAlias);
            if (string.IsNullOrWhiteSpace(normalizedAlias))
                CancelAliasTitleReapplyLocked(hwnd);

            var location = FindLocationForHwndLocked(hwnd);
            if (location is not null && _grids.TryGetValue(location.DeviceName, out var grid))
            {
                grid.Slots[location.Slot] = location.TileSlot with { Alias = normalizedAlias };
                if (applyNow)
                    ApplyAliasTitleWithRetriesLocked(grid.Slots[location.Slot]);
                SlotsChanged?.Invoke();
            }
            else if (applyNow && !string.IsNullOrWhiteSpace(normalizedAlias))
            {
                TrySetWindowTitle(hwnd, normalizedAlias);
                ScheduleAliasTitleReapplyLocked(hwnd);
            }

            return true;
        }
    }

    public AliasReapplyResult ReapplyAliases()
    {
        lock (_lock)
        {
            PruneDeadWindowsLocked();
            var total = new AliasReapplyResult(0, 0, 0, 0);
            foreach (var grid in _grids.Values)
                total = Add(total, ReapplyAliasesLocked(grid));
            return total;
        }
    }

    public AliasReapplyResult ReapplyAliases(nint monitorHandle)
    {
        lock (_lock)
        {
            var grid = GetOrCreateGridLocked(monitorHandle);
            PruneDeadWindowsLocked(grid);
            return ReapplyAliasesLocked(grid);
        }
    }

    private bool RenameSlotLocked(MonitorGridState grid, int slot, string alias)
    {
        if (!grid.Slots.TryGetValue(slot, out var ts))
            return false;
        if (!IsWindow(ts.Hwnd))
        {
            RemoveSlotLocked(grid, slot, clearAlias: true);
            return false;
        }

        var normalizedAlias = NormalizeAlias(alias);
        _aliasMemory.Set(ts.Hwnd, normalizedAlias);
        grid.Slots[slot] = ts with { Alias = normalizedAlias };
        if (!string.IsNullOrWhiteSpace(normalizedAlias))
            ApplyAliasTitleWithRetriesLocked(grid.Slots[slot]);
        else
            CancelAliasTitleReapplyLocked(ts.Hwnd);

        SlotsChanged?.Invoke();
        return true;
    }

    private static string? NormalizeAlias(string? alias) =>
        string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();

    private AliasReapplyResult ReapplyAliasesLocked(MonitorGridState grid)
    {
        var checkedCount = 0;
        var renamed = 0;
        var missing = 0;
        var failed = 0;

        foreach (var tileSlot in grid.Slots.Values)
        {
            if (!IsWindow(tileSlot.Hwnd))
                continue;

            checkedCount++;
            if (string.IsNullOrWhiteSpace(tileSlot.Alias))
            {
                missing++;
                continue;
            }

            if (ApplyAliasTitleWithRetriesLocked(tileSlot))
                renamed++;
            else
                failed++;
        }

        return new AliasReapplyResult(checkedCount, renamed, missing, failed);
    }

    private static AliasReapplyResult Add(AliasReapplyResult a, AliasReapplyResult b) =>
        new(a.Checked + b.Checked, a.Renamed + b.Renamed, a.MissingAlias + b.MissingAlias, a.Failed + b.Failed);

    private bool ApplyAliasTitleLocked(TileSlot tileSlot)
    {
        if (string.IsNullOrWhiteSpace(tileSlot.Alias) || !IsWindow(tileSlot.Hwnd))
            return false;

        return TrySetWindowTitle(tileSlot.Hwnd, tileSlot.Alias);
    }

    private bool ApplyAliasTitleWithRetriesLocked(TileSlot tileSlot)
    {
        var applied = ApplyAliasTitleLocked(tileSlot);
        if (applied)
            ScheduleAliasTitleReapplyLocked(tileSlot.Hwnd);
        return applied;
    }

    private bool ApplyRememberedAliasTitleLocked(nint hwnd)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd))
        {
            CancelAliasTitleReapplyLocked(hwnd);
            return false;
        }

        var location = FindLocationForHwndLocked(hwnd);
        var alias = location?.TileSlot.Alias ?? _aliasMemory.Get(hwnd);
        if (string.IsNullOrWhiteSpace(alias))
        {
            CancelAliasTitleReapplyLocked(hwnd);
            return false;
        }

        if (location is not null && _grids.TryGetValue(location.DeviceName, out var grid))
        {
            grid.Slots[location.Slot] = location.TileSlot with { Alias = alias };
            return ApplyAliasTitleLocked(grid.Slots[location.Slot]);
        }

        return TrySetWindowTitle(hwnd, alias);
    }

    private void ScheduleAliasTitleReapplyLocked(nint hwnd)
    {
        if (hwnd == nint.Zero)
            return;

        if (_aliasTitleReapplyTimers.ContainsKey(hwnd))
            return;

        var timer = new Timer(_ =>
        {
            try
            {
                lock (_lock)
                {
                    if (_disposed || !ApplyRememberedAliasTitleLocked(hwnd))
                        return;
                }
            }
            catch (Exception ex)
            {
                DiagLog($"ALIAS_REAPPLY_ERROR hwnd=0x{hwnd:X}: {ex.Message}");
                lock (_lock)
                    CancelAliasTitleReapplyLocked(hwnd);
            }
        }, null, ALIAS_TITLE_ENFORCE_INTERVAL_MS, ALIAS_TITLE_ENFORCE_INTERVAL_MS);

        _aliasTitleReapplyTimers[hwnd] = timer;
    }

    private void CancelAliasTitleReapplyLocked(nint hwnd)
    {
        if (!_aliasTitleReapplyTimers.Remove(hwnd, out var timer))
            return;

        try { timer.Dispose(); } catch { }
    }

    public void InstallAliasTitleHook()
    {
        lock (_lock)
        {
            try
            {
                if (_nameChangeHook != nint.Zero)
                    return;

                _nameChangeDelegate = OnWindowNameChanged;
                _nameChangeHook = SetWinEventHook(
                    EVENT_OBJECT_NAMECHANGE,
                    EVENT_OBJECT_NAMECHANGE,
                    nint.Zero,
                    _nameChangeDelegate,
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

                if (_nameChangeHook == nint.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    DiagLog($"ALIAS_NAME_HOOK_FAILED err={err}");
                }
            }
            catch (Exception ex)
            {
                DiagLog($"ALIAS_NAME_HOOK_ERROR: {ex.Message}");
                _nameChangeHook = nint.Zero;
                _nameChangeDelegate = null;
            }
        }
    }

    private void OnWindowNameChanged(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == nint.Zero)
            return;

        try
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                var location = FindLocationForHwndLocked(hwnd);
                var alias = location?.TileSlot.Alias ?? _aliasMemory.Get(hwnd);
                if (string.IsNullOrWhiteSpace(alias))
                    return;

                var title = GetWindowTitle(hwnd);
                if (string.Equals(title, alias, StringComparison.Ordinal))
                    return;

                TrySetWindowTitle(hwnd, alias);
            }
        }
        catch (Exception ex)
        {
            DiagLog($"ALIAS_NAME_CHANGE_ERROR hwnd=0x{hwnd:X}: {ex.Message}");
        }
    }

    private bool TrySetWindowTitle(nint hwnd, string title)
    {
        if (SetWindowText(hwnd, title))
            return true;

        var err = Marshal.GetLastWin32Error();
        DiagLog($"SET_WINDOW_TEXT FAILED hwnd=0x{hwnd:X} err={err}");
        return false;
    }

    // ── Rebalance and positioning ───────────────────────────────────────────

    public int FindNextEmptySlot(int? maxSlots = null)
    {
        lock (_lock)
        {
            var grid = GetActiveGridLocked();
            return grid is null ? 0 : FindNextEmptySlotLocked(grid, maxSlots);
        }
    }

    public int FindNextEmptySlot(nint monitorHandle, int? maxSlots = null)
    {
        lock (_lock)
            return FindNextEmptySlotLocked(GetOrCreateGridLocked(monitorHandle), maxSlots);
    }

    public void Rebalance(nint monitorHandle)
    {
        lock (_lock)
        {
            var grid = GetOrCreateGridLocked(monitorHandle);
            PruneDeadWindowsLocked(grid);
            CompactGridLocked(grid);
            PositionAllLocked(grid);
        }
        SlotsChanged?.Invoke();
    }

    public void PositionWindow(int slot, nint monitorHandle)
    {
        lock (_lock)
        {
            var grid = GetOrCreateGridLocked(monitorHandle);
            if (!grid.Slots.TryGetValue(slot, out var tileSlot)) return;
            if (!IsWindow(tileSlot.Hwnd)) { RemoveSlotLocked(grid, slot, clearAlias: true); return; }

            var bounds = GetPaneBoundsForOccupiedSlots(
                grid.MonitorHandle,
                grid.GridSize,
                Layout,
                grid.Slots.Keys.ToArray());
            if (!bounds.TryGetValue(slot, out var rect)) return;
            RestoreWindowForMove(tileSlot.Hwnd);
            TrySetWindowPos(tileSlot.Hwnd, rect, $"single slot={slot}");
            ApplyAliasTitleWithRetriesLocked(tileSlot);
        }
    }

    public void PositionAll(nint monitorHandle)
    {
        lock (_lock)
            PositionAllLocked(GetOrCreateGridLocked(monitorHandle));
    }

    private void PositionAllLocked(MonitorGridState grid)
    {
        if (grid.Slots.Count == 0 || grid.MonitorHandle == nint.Zero)
            return;

        PruneDeadWindowsLocked(grid);
        var bounds = GetPaneBoundsForOccupiedSlots(
            grid.MonitorHandle,
            grid.GridSize,
            Layout,
            grid.Slots.Keys.ToArray());

        var validSlots = grid.Slots.Where(kv => bounds.ContainsKey(kv.Key)).ToList();
        if (validSlots.Count == 0) return;

        var hdwp = BeginDeferWindowPos(validSlots.Count);
        if (hdwp == nint.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            DiagLog($"POSITION_ALL BeginDeferWindowPos FAILED count={validSlots.Count} mon=0x{grid.MonitorHandle:X} err={err}");
            PositionSequentiallyLocked(bounds, validSlots, "begin-fallback");
            SetActiveGridLocked(grid);
            return;
        }

        foreach (var (slot, tileSlot) in validSlots)
        {
            if (!bounds.TryGetValue(slot, out var rect)) continue;
            if (!IsWindow(tileSlot.Hwnd)) continue;

            RestoreWindowForMove(tileSlot.Hwnd);

            hdwp = DeferWindowPos(hdwp, tileSlot.Hwnd, nint.Zero,
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                PositionFlags);

            if (hdwp == nint.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                DiagLog($"POSITION_ALL DeferWindowPos FAILED slot={slot} hwnd=0x{tileSlot.Hwnd:X} err={err}");
                PositionSequentiallyLocked(bounds, validSlots, "defer-fallback");
                SetActiveGridLocked(grid);
                return;
            }
        }

        if (!EndDeferWindowPos(hdwp))
        {
            var err = Marshal.GetLastWin32Error();
            DiagLog($"POSITION_ALL EndDeferWindowPos FAILED count={validSlots.Count} err={err}");
            PositionSequentiallyLocked(bounds, validSlots, "end-fallback");
        }
        ReapplyAliasesLocked(grid);
        SetActiveGridLocked(grid);
    }

    private bool TrySetWindowPos(nint hwnd, RECT rect, string context)
    {
        var ok = SetWindowPos(hwnd, nint.Zero,
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                PositionFlags);
        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            DiagLog($"SET_WINDOW_POS FAILED {context} hwnd=0x{hwnd:X} err={err} rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})");
        }
        return ok;
    }

    private void PositionSequentiallyLocked(
        IReadOnlyDictionary<int, RECT> bounds,
        IReadOnlyList<KeyValuePair<int, TileSlot>> validSlots,
        string reason)
    {
        foreach (var (slot, tileSlot) in validSlots)
        {
            if (!bounds.TryGetValue(slot, out var rect)) continue;
            if (!IsWindow(tileSlot.Hwnd)) continue;

            RestoreWindowForMove(tileSlot.Hwnd);
            TrySetWindowPos(tileSlot.Hwnd, rect, $"{reason} slot={slot}");
            ApplyAliasTitleWithRetriesLocked(tileSlot);
        }
    }

    private static void RestoreWindowForMove(nint hwnd)
    {
        if (IsIconic(hwnd) || IsZoomed(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
    }

    // ── Grid geometry ───────────────────────────────────────────────────────

    public static Dictionary<int, RECT> GetPaneBounds(nint monitorHandle, int gridSize, string layout = "2x3")
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitorHandle, ref info);
        return GetPaneBounds(monitorHandle, gridSize, info.rcWork, layout);
    }

    public static Dictionary<int, RECT> GetPaneBounds(nint monitorHandle, int gridSize, RECT wa, string layout = "2x3")
    {
        int w = wa.Right - wa.Left;
        int h = wa.Bottom - wa.Top;
        var result = new Dictionary<int, RECT>();

        switch (NormalizeGridSize(gridSize))
        {
            case 1:
                result[0] = new RECT(wa.Left, wa.Top, wa.Right, wa.Bottom);
                break;
            case 2:
                result[0] = new RECT(wa.Left, wa.Top, wa.Left + w / 2, wa.Bottom);
                result[1] = new RECT(wa.Left + w / 2, wa.Top, wa.Right, wa.Bottom);
                break;
            case 4:
                int hw = w / 2, hh = h / 2;
                result[0] = new RECT(wa.Left, wa.Top, wa.Left + hw, wa.Top + hh);
                result[1] = new RECT(wa.Left + hw, wa.Top, wa.Right, wa.Top + hh);
                result[2] = new RECT(wa.Left, wa.Top + hh, wa.Left + hw, wa.Bottom);
                result[3] = new RECT(wa.Left + hw, wa.Top + hh, wa.Right, wa.Bottom);
                break;
            case 6 when layout == "3x2":
                int cw = w / 2;
                int rh = h / 3;
                result[0] = new RECT(wa.Left, wa.Top, wa.Left + cw, wa.Top + rh);
                result[1] = new RECT(wa.Left + cw, wa.Top, wa.Right, wa.Top + rh);
                result[2] = new RECT(wa.Left, wa.Top + rh, wa.Left + cw, wa.Top + 2 * rh);
                result[3] = new RECT(wa.Left + cw, wa.Top + rh, wa.Right, wa.Top + 2 * rh);
                result[4] = new RECT(wa.Left, wa.Top + 2 * rh, wa.Left + cw, wa.Bottom);
                result[5] = new RECT(wa.Left + cw, wa.Top + 2 * rh, wa.Right, wa.Bottom);
                break;
            case 6:
                int tw = w / 3;
                int th = h / 2;
                result[0] = new RECT(wa.Left, wa.Top, wa.Left + tw, wa.Top + th);
                result[1] = new RECT(wa.Left + tw, wa.Top, wa.Left + 2 * tw, wa.Top + th);
                result[2] = new RECT(wa.Left + 2 * tw, wa.Top, wa.Right, wa.Top + th);
                result[3] = new RECT(wa.Left, wa.Top + th, wa.Left + tw, wa.Bottom);
                result[4] = new RECT(wa.Left + tw, wa.Top + th, wa.Left + 2 * tw, wa.Bottom);
                result[5] = new RECT(wa.Left + 2 * tw, wa.Top + th, wa.Right, wa.Bottom);
                break;
        }

        return result;
    }

    public static Dictionary<int, RECT> GetPaneBoundsForOccupiedSlots(
        nint monitorHandle,
        int gridSize,
        string layout,
        IReadOnlyCollection<int> occupiedSlots)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitorHandle, ref info);
        return GetPaneBoundsForOccupiedSlots(monitorHandle, gridSize, info.rcWork, layout, occupiedSlots);
    }

    public static Dictionary<int, RECT> GetPaneBoundsForOccupiedSlots(
        nint monitorHandle,
        int gridSize,
        RECT wa,
        string layout,
        IReadOnlyCollection<int> occupiedSlots)
    {
        var result = GetPaneBounds(monitorHandle, gridSize, wa, layout);
        ApplyOddWindowSpan(result, NormalizeGridSize(gridSize), layout, occupiedSlots);
        return result;
    }

    private static void ApplyOddWindowSpan(
        Dictionary<int, RECT> bounds,
        int gridSize,
        string layout,
        IReadOnlyCollection<int> occupiedSlots)
    {
        if (!string.Equals(layout, "2x3", StringComparison.OrdinalIgnoreCase))
            return;

        var occupied = occupiedSlots
            .Where(bounds.ContainsKey)
            .ToHashSet();

        if (gridSize == 4 && occupied.Count == 3)
        {
            SpanAcrossSingleEmptyRow(bounds, occupied, 0, 2);
            SpanAcrossSingleEmptyRow(bounds, occupied, 1, 3);
            return;
        }

        if (gridSize == 6 && occupied.Count == 5)
        {
            SpanAcrossSingleEmptyRow(bounds, occupied, 0, 3);
            SpanAcrossSingleEmptyRow(bounds, occupied, 1, 4);
            SpanAcrossSingleEmptyRow(bounds, occupied, 2, 5);
        }
    }

    private static void SpanAcrossSingleEmptyRow(
        Dictionary<int, RECT> bounds,
        HashSet<int> occupied,
        int topSlot,
        int bottomSlot)
    {
        if (!bounds.TryGetValue(topSlot, out var top) ||
            !bounds.TryGetValue(bottomSlot, out var bottom))
            return;

        var topOccupied = occupied.Contains(topSlot);
        var bottomOccupied = occupied.Contains(bottomSlot);
        if (topOccupied == bottomOccupied)
            return;

        var spanSlot = topOccupied ? topSlot : bottomSlot;
        bounds[spanSlot] = new RECT(top.Left, top.Top, bottom.Right, bottom.Bottom);
    }

    // ── HWND discovery ──────────────────────────────────────────────────────

    private static IReadOnlyList<MonitorHandleInfo> EnumerateMonitorHandles(IReadOnlyCollection<string>? enabledDeviceNames)
    {
        var enabledAll = enabledDeviceNames is null || enabledDeviceNames.Count == 0;
        var enabled = enabledAll
            ? null
            : enabledDeviceNames!.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var monitors = new List<MonitorHandleInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EnumDisplayMonitors(nint.Zero, nint.Zero, (hMon, _, _, _) =>
        {
            var deviceName = MonitorEnumerator.GetDeviceName(hMon);
            if (string.IsNullOrWhiteSpace(deviceName))
                return true;
            if (!enabledAll && enabled?.Contains(deviceName) != true)
                return true;
            if (!seen.Add(deviceName))
                return true;

            monitors.Add(new MonitorHandleInfo(deviceName, hMon));
            return true;
        }, nint.Zero);

        return monitors;
    }

    private static IReadOnlyList<VisibleTerminalInfo> EnumerateVisibleTerminalWindows(IReadOnlyList<MonitorHandleInfo> enabledMonitors)
    {
        var enabled = enabledMonitors
            .Select(m => m.DeviceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var raw = new List<(nint Hwnd, string DeviceName, RECT Rect)>();
        var seen = new HashSet<nint>();

        EnumWindows((candidateHwnd, _) =>
        {
            if (!seen.Add(candidateHwnd))
                return true;
            if (GetWindow(candidateHwnd, GW_OWNER) != nint.Zero)
                return true;
            if (IsIconic(candidateHwnd))
                return true;
            if (!TerminalDetector.IsTerminalWindow(candidateHwnd))
                return true;
            if (!GetWindowRect(candidateHwnd, out var rect))
                return true;

            var cx = rect.Left + ((rect.Right - rect.Left) / 2);
            var cy = rect.Top + ((rect.Bottom - rect.Top) / 2);
            var monitor = GetMonitorAtPoint(cx, cy);
            var deviceName = MonitorEnumerator.GetDeviceName(monitor);
            if (deviceName is null || !enabled.Contains(deviceName))
                return true;

            raw.Add((candidateHwnd, deviceName, rect));
            return true;
        }, nint.Zero);

        return raw
            .GroupBy(w => w.DeviceName, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group
                .OrderBy(w => w.Rect.Top)
                .ThenBy(w => w.Rect.Left)
                .Select((w, index) => new VisibleTerminalInfo(w.Hwnd, w.DeviceName, index)))
            .ToArray();
    }

    public static nint FindWindowForProcess(Process process)
    {
        var hwnd = process.MainWindowHandle;
        if (hwnd != nint.Zero && IsWindow(hwnd))
            return hwnd;

        nint found = nint.Zero;
        EnumWindows((candidateHwnd, _) =>
        {
            GetWindowThreadProcessId(candidateHwnd, out int pid);
            if (pid != process.Id) return true;
            if (!IsWindowVisible(candidateHwnd)) return true;
            if (GetWindow(candidateHwnd, GW_OWNER) != nint.Zero) return true;
            var exStyle = GetWindowLong(candidateHwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            found = candidateHwnd;
            return false;
        }, nint.Zero);

        return found;
    }

    private static IReadOnlyList<nint> EnumerateVisibleTerminalWindowsOnMonitor(nint monitorHandle)
    {
        var windows = new List<(nint Hwnd, RECT Rect)>();
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitorHandle, ref info);
        var work = info.rcWork;

        EnumWindows((candidateHwnd, _) =>
        {
            if (GetWindow(candidateHwnd, GW_OWNER) != nint.Zero)
                return true;
            if (!TerminalDetector.IsTerminalWindow(candidateHwnd))
                return true;
            if (!GetWindowRect(candidateHwnd, out var rect))
                return true;

            var cx = rect.Left + ((rect.Right - rect.Left) / 2);
            var cy = rect.Top + ((rect.Bottom - rect.Top) / 2);
            if (cx < work.Left || cx >= work.Right || cy < work.Top || cy >= work.Bottom)
                return true;

            windows.Add((candidateHwnd, rect));
            return true;
        }, nint.Zero);

        return windows
            .OrderBy(w => w.Rect.Top)
            .ThenBy(w => w.Rect.Left)
            .Select(w => w.Hwnd)
            .ToArray();
    }

    private static bool TryGetWindowCenter(nint hwnd, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!GetWindowRect(hwnd, out var rect))
            return false;

        x = rect.Left + ((rect.Right - rect.Left) / 2);
        y = rect.Top + ((rect.Bottom - rect.Top) / 2);
        return true;
    }

    public static int HitTestSlot(int screenX, int screenY, Dictionary<int, RECT>? bounds)
    {
        if (bounds is null)
            return -1;

        foreach (var (slot, rect) in bounds)
        {
            if (screenX >= rect.Left && screenX < rect.Right &&
                screenY >= rect.Top && screenY < rect.Bottom)
                return slot;
        }
        return -1;
    }

    public static nint GetMonitorAtCursor()
    {
        GetCursorPos(out var pt);
        return MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
    }

    public static nint GetMonitorAtPoint(int screenX, int screenY)
    {
        return MonitorFromPoint(new POINT { X = screenX, Y = screenY }, MONITOR_DEFAULTTONEAREST);
    }

    public string? GetDeviceName(nint monitorHandle)
    {
        lock (_lock)
            return GetOrCreateGridLocked(monitorHandle).DeviceName;
    }

    // ── Internal helpers ────────────────────────────────────────────────────

    private MonitorGridSnapshot SnapshotForGridLocked(MonitorGridState? grid)
    {
        if (grid is null)
            return new MonitorGridSnapshot("", nint.Zero, new Dictionary<int, TileSlot>(), 1);

        PruneDeadWindowsLocked(grid);
        return new MonitorGridSnapshot(
            grid.DeviceName,
            grid.MonitorHandle,
            new Dictionary<int, TileSlot>(grid.Slots),
            grid.GridSize);
    }

    private MonitorGridState? GetActiveGridLocked()
    {
        if (_activeMonitorDevice is not null && _grids.TryGetValue(_activeMonitorDevice, out var active))
            return active;

        return _grids.Values.FirstOrDefault(g => g.Slots.Count > 0);
    }

    private MonitorGridState GetOrCreateGridLocked(nint monitorHandle)
    {
        var deviceName = MonitorEnumerator.GetDeviceName(monitorHandle) ?? $"monitor:0x{monitorHandle:X}";
        if (!_grids.TryGetValue(deviceName, out var grid))
        {
            grid = new MonitorGridState { DeviceName = deviceName, MonitorHandle = monitorHandle };
            _grids[deviceName] = grid;
        }
        else if (monitorHandle != nint.Zero)
        {
            grid.MonitorHandle = monitorHandle;
        }

        return grid;
    }

    private void SetActiveGridLocked(MonitorGridState grid)
    {
        _activeMonitorDevice = grid.DeviceName;
        _activeMonitor = grid.MonitorHandle;
    }

    private TileLocation? FindLocationForHwndLocked(nint hwnd)
    {
        foreach (var grid in _grids.Values)
        {
            foreach (var kv in grid.Slots)
            {
                if (kv.Value.Hwnd == hwnd)
                    return new TileLocation(grid.DeviceName, grid.MonitorHandle, kv.Key, kv.Value);
            }
        }
        return null;
    }

    private bool RestoreDetachedWindowLocked(nint hwnd, out nint monitor)
    {
        monitor = nint.Zero;
        if (!_detachedTiles.Remove(hwnd, out var detached))
            return false;

        if (!IsWindow(hwnd))
        {
            _aliasMemory.Remove(hwnd);
            return false;
        }

        var grid = GetOrCreateGridLocked(detached.MonitorHandle);
        var slot = detached.Slot;
        if (grid.Slots.ContainsKey(slot))
            slot = FindNextEmptySlotLocked(grid, PageMovePlanner.MaxWindowsPerGrid);
        if (slot < 0)
        {
            _detachedTiles[hwnd] = detached;
            return false;
        }

        var alias = detached.TileSlot.Alias ?? _aliasMemory.Get(hwnd);
        grid.Slots[slot] = detached.TileSlot with { Alias = alias };
        ApplyAliasTitleWithRetriesLocked(grid.Slots[slot]);
        RegisterProcessExitLocked(hwnd, detached.TileSlot.ProcessId);
        SetActiveGridLocked(grid);
        monitor = grid.MonitorHandle;
        DiagLog($"DETACHED_RESTORED device={grid.DeviceName} slot={slot} hwnd=0x{hwnd:X}");
        return true;
    }

    private int ReattachDetachedTilesLocked(MonitorGridState grid)
    {
        var matches = _detachedTiles
            .Where(kv => string.Equals(kv.Value.DeviceName, grid.DeviceName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.Slot)
            .ToList();

        var restored = 0;
        foreach (var (hwnd, _) in matches)
        {
            if (grid.Slots.Count >= PageMovePlanner.MaxWindowsPerGrid)
                break;
            if (RestoreDetachedWindowLocked(hwnd, out _))
                restored++;
        }

        return restored;
    }

    private int RecoverVisibleTerminalWindowsLocked(MonitorGridState grid)
    {
        var candidates = EnumerateVisibleTerminalWindowsOnMonitor(grid.MonitorHandle)
            .Where(hwnd => FindLocationForHwndLocked(hwnd) is null)
            .Take(PageMovePlanner.MaxWindowsPerGrid)
            .ToArray();

        if (candidates.Length == 0)
            return 0;

        var gridSize = ComputeGridSize(candidates.Length);
        var bounds = GetPaneBounds(grid.MonitorHandle, gridSize, Layout);
        var recovered = 0;

        foreach (var hwnd in candidates)
        {
            if (grid.Slots.Count >= PageMovePlanner.MaxWindowsPerGrid)
                break;
            if (!TryGetWindowCenter(hwnd, out var x, out var y))
                continue;

            var slot = HitTestSlot(x, y, bounds);
            if (slot < 0 || grid.Slots.ContainsKey(slot))
                slot = FindNextEmptySlotLocked(grid, gridSize);
            if (slot < 0)
                continue;

            GetWindowThreadProcessId(hwnd, out int pid);
            var alias = _aliasMemory.Get(hwnd);
            var tileSlot = new TileSlot(hwnd, pid, null, alias);
            grid.Slots[slot] = tileSlot;
            ApplyAliasTitleWithRetriesLocked(tileSlot);
            RegisterProcessExitLocked(hwnd, pid);
            recovered++;
            DiagLog($"GRID_RECOVERED device={grid.DeviceName} slot={slot} hwnd=0x{hwnd:X}");
        }

        return recovered;
    }

    private void PruneDetachedTilesLocked()
    {
        foreach (var hwnd in _detachedTiles.Keys.Where(hwnd => !IsWindow(hwnd)).ToList())
        {
            _detachedTiles.Remove(hwnd);
            _aliasMemory.Remove(hwnd);
        }
    }

    private bool RemoveHwndLocked(nint hwnd, bool clearAlias)
    {
        foreach (var grid in _grids.Values)
        {
            var slot = grid.Slots.FirstOrDefault(kv => kv.Value.Hwnd == hwnd);
            if (slot.Value is not null)
            {
                if (clearAlias)
                    _detachedTiles.Remove(hwnd);
                return RemoveSlotLocked(grid, slot.Key, clearAlias);
            }
        }
        return false;
    }

    private bool RemoveSlotLocked(MonitorGridState grid, int slot, bool clearAlias)
    {
        if (!grid.Slots.TryGetValue(slot, out var tileSlot))
            return false;

        grid.Slots.Remove(slot);
        if (clearAlias)
        {
            _aliasMemory.Remove(tileSlot.Hwnd);
            CancelAliasTitleReapplyLocked(tileSlot.Hwnd);
        }

        if (!_aliasMemory.Contains(tileSlot.Hwnd) &&
            _processWaits.TryGetValue(tileSlot.Hwnd, out var proc))
        {
            try { proc.Dispose(); } catch { }
            _processWaits.Remove(tileSlot.Hwnd);
        }

        DiagLog($"SLOT_REMOVED device={grid.DeviceName} slot={slot} hwnd=0x{tileSlot.Hwnd:X} remaining={grid.Slots.Count}");
        _logger.LogInformation("KH: Removed HWND {Hwnd} from {Device} slot {Slot}", tileSlot.Hwnd, grid.DeviceName, slot);
        return true;
    }

    private void RegisterProcessExitLocked(nint hwnd, int pid)
    {
        if (_processWaits.ContainsKey(hwnd)) return;

        try
        {
            var proc = Process.GetProcessById(pid);
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                try
                {
                    DiagLog($"PROC_EXIT hwnd=0x{hwnd:X} pid={pid} removing from grids and alias memory");
                    lock (_lock)
                    {
                        RemoveHwndLocked(hwnd, clearAlias: true);
                        _aliasMemory.Remove(hwnd);
                        CancelAliasTitleReapplyLocked(hwnd);
                        if (_processWaits.Remove(hwnd, out var stored))
                            stored.Dispose();
                    }
                    SlotsChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    DiagLog($"PROC_EXIT_ERROR hwnd=0x{hwnd:X} pid={pid}: {ex.Message}");
                }
            };
            _processWaits[hwnd] = proc;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: Could not register process exit watch for PID {Pid}", pid);
        }
    }

    private bool PruneDeadWindowsLocked()
    {
        var pruned = false;
        foreach (var grid in _grids.Values)
            pruned |= PruneDeadWindowsLocked(grid);

        return pruned;
    }

    private bool PruneDeadWindowsLocked(MonitorGridState grid)
    {
        var deadSlots = grid.Slots.Where(kv => !IsWindow(kv.Value.Hwnd)).Select(kv => kv.Key).ToList();
        foreach (var slot in deadSlots)
            RemoveSlotLocked(grid, slot, clearAlias: true);
        return deadSlots.Count > 0;
    }

    private void CompactGridLocked(MonitorGridState grid)
    {
        var ordered = GetOrderedTileSlots(grid).ToArray();
        RewriteGridSlotsLocked(grid, ordered);
    }

    private void RewriteGridSlotsLocked(MonitorGridState grid, IReadOnlyList<TileSlot> slots)
    {
        grid.Slots.Clear();
        var fillOrder = GetFillOrder(ComputeGridSize(slots.Count), Layout);
        for (int i = 0; i < slots.Count && i < fillOrder.Count; i++)
            grid.Slots[fillOrder[i]] = slots[i];
    }

    private int FindNextEmptySlotLocked(MonitorGridState grid, int? maxSlots = null)
    {
        int max = maxSlots ?? grid.GridSizeForNextWindow;
        foreach (var slot in GetFillOrder(max, Layout))
            if (!grid.Slots.ContainsKey(slot)) return slot;
        return -1;
    }

    private IEnumerable<TileSlot> GetOrderedTileSlots(MonitorGridState grid)
    {
        var slotOrder = GetFillOrder(grid.GridSize, Layout);
        return grid.Slots
            .OrderBy(kv =>
            {
                var index = IndexOfSlot(slotOrder, kv.Key);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(kv => kv.Key)
            .Select(kv => kv.Value);
    }

    private static int IndexOfSlot(IReadOnlyList<int> slotOrder, int slot)
    {
        for (int i = 0; i < slotOrder.Count; i++)
            if (slotOrder[i] == slot)
                return i;
        return -1;
    }

    public static IReadOnlyList<int> GetFillOrder(int gridSize, string layout = "2x3")
    {
        return NormalizeGridSize(gridSize) switch
        {
            4 => [0, 2, 1, 3],
            6 when string.Equals(layout, "3x2", StringComparison.OrdinalIgnoreCase) => [0, 2, 4, 1, 3, 5],
            6 => [0, 3, 1, 4, 2, 5],
            2 => [0, 1],
            _ => [0],
        };
    }

    public static int NormalizeGridSize(int size) => size switch
    {
        0 or 1 => 1,
        2 => 2,
        6 => 6,
        _ => 4,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _reflowDebounce?.Dispose();

        lock (_lock)
        {
            if (_nameChangeHook != nint.Zero)
            {
                try { UnhookWinEvent(_nameChangeHook); } catch { }
                _nameChangeHook = nint.Zero;
            }
            _nameChangeDelegate = null;

            foreach (var timer in _aliasTitleReapplyTimers.Values)
                try { timer.Dispose(); } catch { }
            _aliasTitleReapplyTimers.Clear();
            foreach (var proc in _processWaits.Values)
                try { proc.Dispose(); } catch { }
            _processWaits.Clear();
            _grids.Clear();
        }
    }

    // ── PInvoke ─────────────────────────────────────────────────────────────

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    private const uint PositionFlags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const int SW_RESTORE = 9;
    private const int GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] public static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(nint hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint BeginDeferWindowPos(int nNumWindows);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint DeferWindowPos(nint hWinPosInfo, nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EndDeferWindowPos(nint hWinPosInfo);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, int dwFlags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hWnd, int uCmd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, nint lprcMonitor, nint dwData);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(nint hWnd, string lpString);

    private delegate void WinEventDelegate(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    public static string GetWindowTitle(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static readonly string _diagLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "TinyBoss", "drag_diag.log");

    internal static void DiagLog(string msg)
    {
        try { File.AppendAllText(_diagLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { /* best-effort */ }
    }
}
