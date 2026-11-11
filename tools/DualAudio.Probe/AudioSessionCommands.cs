using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DualAudio.Probe;

/// <summary>A render endpoint as seen at one moment. Strings are already redacted.</summary>
internal sealed record EndpointInfo(
    string Id,
    string FriendlyName,
    string State,
    bool IsDefaultConsole,
    bool IsDefaultMultimedia,
    bool IsDefaultCommunications);

/// <summary>One audio session as seen at one moment. Strings are already redacted.</summary>
internal sealed record SessionInfo(
    int Index,
    string EndpointId,
    string EndpointName,
    uint? ProcessId,
    string ProcessName,
    string? DisplayName,
    string? IconPath,
    string? SessionIdentifier,
    string? SessionInstanceIdentifier,
    bool? IsSystemSoundsSession,
    string State,
    float? Volume,
    bool? Mute,
    float? PeakAtCapture,
    List<string> ReadErrors);

internal sealed record SessionSnapshot(
    string Name,
    DateTimeOffset TakenAt,
    List<EndpointInfo> Endpoints,
    List<SessionInfo> Sessions);

/// <summary>
/// Read-only inspection of WASAPI audio sessions on render endpoints, plus volume/mute on ONE
/// session the user picks by index. Every command creates and disposes its own COM wrappers.
/// </summary>
internal sealed partial class AudioSessionCommands
{
    /// <summary>
    /// What 'setvol'/'mute' act on: the session the user saw in the last listing. The raw (unredacted)
    /// instance identifier is kept in memory only, to re-find exactly that session later.
    /// </summary>
    private sealed record SessionTarget(int Index, string EndpointId, string RawInstanceId, string Label);

    private readonly ProbeLog _log;
    private readonly string _snapshotDir = Path.Combine(AppContext.BaseDirectory, "snapshots");
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly object _targetsLock = new();
    private List<SessionTarget> _targets = [];

    public AudioSessionCommands(ProbeLog log) => _log = log;

    // All COM work runs on a thread-pool (MTA) thread.
    public Task EndpointsAsync() => Task.Run(Endpoints);
    public Task SessionsAsync(bool all) => Task.Run(() => Sessions(all));
    public Task SnapshotAsync(string name) => Task.Run(() => Snapshot(name));
    public Task DiffAsync(string a, string b) => Task.Run(() => Diff(a, b));
    public Task SetVolumeAsync(int index, int percent) => Task.Run(() => SetVolume(index, percent));
    public Task SetMuteAsync(int index, bool mute) => Task.Run(() => SetMute(index, mute));

    // ---------------------------------------------------------------- endpoints

    private void Endpoints()
    {
        using var en = new MMDeviceEnumerator();
        var defaults = ReadDefaults(en);
        using var devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
        _log.Info($"Render endpoints (all states): {devices.Count}");
        for (var i = 0; i < devices.Count; i++)
        {
            using var dev = devices[i];
            var info = ReadEndpoint(dev, defaults);
            _log.Info($"  [{i}] \"{info.FriendlyName}\" State={info.State}{DefaultTag(info)}");
            _log.Info($"       Id={info.Id}");
        }
        LogDefaults(defaults);
    }

    // ---------------------------------------------------------------- sessions / snapshot

    private void Sessions(bool all)
    {
        var (endpoints, sessions, targets) = Scan(all);
        PrintSessions(endpoints, sessions);
        SetTargets(targets);
    }

    private void Snapshot(string name)
    {
        if (!SnapshotName().IsMatch(name))
        {
            _log.Warn("Snapshot name may only contain letters, digits, '-', '_' (max 64).");
            return;
        }

        // Snapshots always cover every active render endpoint, so a session that appears on a
        // non-default endpoint is not missed by 'diff'.
        var (endpoints, sessions, targets) = Scan(all: true);
        PrintSessions(endpoints, sessions);
        SetTargets(targets);

        var snapshot = new SessionSnapshot(name, DateTimeOffset.Now, endpoints, sessions);
        Directory.CreateDirectory(_snapshotDir);
        var path = Path.Combine(_snapshotDir, name + ".json");
        if (File.Exists(path)) _log.Warn($"Overwriting existing snapshot '{name}'.");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, _json));
        _log.Ok($"Snapshot '{name}' saved: {sessions.Count} session(s) on {endpoints.Count} endpoint(s) -> {path}");
    }

    /// <summary>Enumerates render endpoints (default Multimedia only, or all active) and their sessions.</summary>
    private (List<EndpointInfo>, List<SessionInfo>, List<SessionTarget>) Scan(bool all)
    {
        var endpoints = new List<EndpointInfo>();
        var sessions = new List<SessionInfo>();
        var targets = new List<SessionTarget>();

        using var en = new MMDeviceEnumerator();
        var defaults = ReadDefaults(en);

        if (all)
        {
            using var devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (var i = 0; i < devices.Count; i++)
            {
                using var dev = devices[i];
                ScanEndpoint(dev, defaults, endpoints, sessions, targets);
            }
        }
        else if (en.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var dev) && dev is not null)
        {
            using (dev) ScanEndpoint(dev, defaults, endpoints, sessions, targets);
            if (defaults.Console != defaults.Multimedia)
                _log.Warn("Default Console and Multimedia endpoints differ; showing Multimedia. Use 'sessions all' to see both.");
        }
        else
        {
            _log.Warn("No default render endpoint.");
        }

        return (endpoints, sessions, targets);
    }

    private void ScanEndpoint(
        MMDevice dev, Defaults defaults,
        List<EndpointInfo> endpoints, List<SessionInfo> sessions, List<SessionTarget> targets)
    {
        var endpoint = ReadEndpoint(dev, defaults);
        endpoints.Add(endpoint);

        SessionCollection collection;
        try
        {
            var manager = dev.AudioSessionManager; // owned and disposed by dev
            manager.RefreshSessions();
            collection = manager.Sessions;
        }
        catch (Exception ex)
        {
            _log.Exception($"Session enumeration on \"{endpoint.FriendlyName}\"", ex);
            return;
        }

        for (var i = 0; i < collection.Count; i++)
        {
            AudioSessionControl s;
            try { s = collection[i]; }
            catch (Exception ex)
            {
                _log.Exception($"Get session {i} on \"{endpoint.FriendlyName}\"", ex);
                continue;
            }

            using (s)
            {
                var index = sessions.Count;
                var errors = new List<string>();
                var pid = ReadV(() => s.GetProcessID, "ProcessID", errors);
                var rawInstanceId = ReadR(() => s.GetSessionInstanceIdentifier, "GetSessionInstanceIdentifier", errors);

                var info = new SessionInfo(
                    Index: index,
                    EndpointId: endpoint.Id,
                    EndpointName: endpoint.FriendlyName,
                    ProcessId: pid,
                    ProcessName: pid is { } p ? ProcessName(p) : "?",
                    DisplayName: Redact.Addresses(ReadR(() => s.DisplayName, "DisplayName", errors)),
                    IconPath: Redact.Addresses(ReadR(() => s.IconPath, "IconPath", errors)),
                    SessionIdentifier: Redact.Addresses(ReadR(() => s.GetSessionIdentifier, "GetSessionIdentifier", errors)),
                    SessionInstanceIdentifier: Redact.Addresses(rawInstanceId),
                    IsSystemSoundsSession: ReadV(() => s.IsSystemSoundsSession, "IsSystemSoundsSession", errors),
                    State: ReadV(() => s.State, "State", errors) is { } st ? StateName(st) : "?",
                    Volume: ReadV(() => s.SimpleAudioVolume.Volume, "SimpleAudioVolume.Volume", errors),
                    Mute: ReadV(() => s.SimpleAudioVolume.Mute, "SimpleAudioVolume.Mute", errors),
                    PeakAtCapture: ReadV(() => s.AudioMeterInformation.MasterPeakValue, "MasterPeakValue", errors),
                    ReadErrors: errors);
                sessions.Add(info);

                if (rawInstanceId is not null)
                    targets.Add(new SessionTarget(index, RawEndpointId(dev), rawInstanceId,
                        $"pid {info.ProcessId} ({info.ProcessName}) \"{info.DisplayName}\" on \"{endpoint.FriendlyName}\""));
            }
        }
    }

    private void PrintSessions(List<EndpointInfo> endpoints, List<SessionInfo> sessions)
    {
        foreach (var ep in endpoints)
        {
            var mine = sessions.Where(s => s.EndpointId == ep.Id).ToList();
            _log.Info($"Endpoint \"{ep.FriendlyName}\"{DefaultTag(ep)} - {mine.Count} session(s)");
            _log.Info($"  Id={ep.Id}");
            foreach (var s in mine) PrintSession(s, "  ");
        }
        _log.Info("Use the [index] with 'setvol' / 'mute'. Peak = output level at the moment of listing (0..1).");
    }

    private void PrintSession(SessionInfo s, string indent)
    {
        _log.Info($"{indent}[{s.Index}] pid={s.ProcessId?.ToString() ?? "?"} ({s.ProcessName}) State={s.State} " +
                  $"Vol={Percent(s.Volume)} Mute={Show(s.Mute)} Peak={s.PeakAtCapture?.ToString("0.000") ?? "?"} " +
                  $"SystemSounds={Show(s.IsSystemSoundsSession)}");
        _log.Info($"{indent}     DisplayName=\"{s.DisplayName}\" IconPath=\"{s.IconPath}\"");
        _log.Info($"{indent}     SessionIdentifier={s.SessionIdentifier}");
        _log.Info($"{indent}     SessionInstanceIdentifier={s.SessionInstanceIdentifier}");
        if (s.ReadErrors.Count > 0)
            _log.Warn($"{indent}     read errors: {string.Join("; ", s.ReadErrors)}");
    }

    // ---------------------------------------------------------------- diff

    private void Diff(string nameA, string nameB)
    {
        var a = LoadSnapshot(nameA);
        var b = LoadSnapshot(nameB);
        if (a is null || b is null) return;

        _log.Info($"Diff '{a.Name}' ({a.TakenAt:HH:mm:ss}) -> '{b.Name}' ({b.TakenAt:HH:mm:ss})");

        var defA = a.Endpoints.FirstOrDefault(e => e.IsDefaultMultimedia)?.FriendlyName ?? "(none)";
        var defB = b.Endpoints.FirstOrDefault(e => e.IsDefaultMultimedia)?.FriendlyName ?? "(none)";
        if (defA != defB) _log.Warn($"Default Multimedia endpoint changed: \"{defA}\" -> \"{defB}\"");

        foreach (var ep in b.Endpoints.Where(e => a.Endpoints.All(x => x.Id != e.Id)))
            _log.Info($"Endpoint added:   \"{ep.FriendlyName}\" Id={ep.Id}");
        foreach (var ep in a.Endpoints.Where(e => b.Endpoints.All(x => x.Id != e.Id)))
            _log.Info($"Endpoint removed: \"{ep.FriendlyName}\" Id={ep.Id}");

        var mapA = ByKey(a);
        var mapB = ByKey(b);

        var added = mapB.Keys.Except(mapA.Keys).Select(k => mapB[k]).ToList();
        var removed = mapA.Keys.Except(mapB.Keys).Select(k => mapA[k]).ToList();

        _log.Info($"Sessions ADDED ({added.Count}):");
        foreach (var s in added) PrintSession(s, "  ");
        _log.Info($"Sessions REMOVED ({removed.Count}):");
        foreach (var s in removed) PrintSession(s, "  ");

        var changedCount = 0;
        _log.Info("Sessions CHANGED (Peak ignored):");
        foreach (var key in mapA.Keys.Intersect(mapB.Keys))
        {
            var changes = Changes(mapA[key], mapB[key]);
            if (changes.Count == 0) continue;
            changedCount++;
            var s = mapB[key];
            _log.Info($"  pid={s.ProcessId} ({s.ProcessName}) \"{s.DisplayName}\" on \"{s.EndpointName}\"");
            _log.Info($"     SessionInstanceIdentifier={s.SessionInstanceIdentifier}");
            foreach (var c in changes) _log.Info($"     {c}");
        }
        if (changedCount == 0) _log.Info("  (none)");

        _log.Info($"Summary: {added.Count} added, {removed.Count} removed, {changedCount} changed, " +
                  $"{mapA.Keys.Intersect(mapB.Keys).Count() - changedCount} unchanged.");
        _log.Info("Note: indexes in a diff are from the snapshot; run 'sessions all' to get current indexes for setvol/mute.");
    }

    private Dictionary<string, SessionInfo> ByKey(SessionSnapshot snap)
    {
        var map = new Dictionary<string, SessionInfo>();
        foreach (var s in snap.Sessions)
        {
            // SessionInstanceIdentifier is unique per session instance; scope it by endpoint anyway.
            var key = $"{s.EndpointId}|{s.SessionInstanceIdentifier ?? $"(no id #{s.Index})"}";
            if (!map.TryAdd(key, s))
                _log.Warn($"Snapshot '{snap.Name}': duplicate session key, keeping the first: {key}");
        }
        return map;
    }

    private static List<string> Changes(SessionInfo a, SessionInfo b)
    {
        var list = new List<string>();
        void Cmp(string field, object? x, object? y)
        {
            if (!Equals(x, y)) list.Add($"{field}: {x ?? "null"} -> {y ?? "null"}");
        }
        Cmp("State", a.State, b.State);
        Cmp("Volume", Percent(a.Volume), Percent(b.Volume));
        Cmp("Mute", a.Mute, b.Mute);
        Cmp("ProcessId", a.ProcessId, b.ProcessId);
        Cmp("ProcessName", a.ProcessName, b.ProcessName);
        Cmp("DisplayName", a.DisplayName, b.DisplayName);
        Cmp("IconPath", a.IconPath, b.IconPath);
        Cmp("SessionIdentifier", a.SessionIdentifier, b.SessionIdentifier);
        Cmp("IsSystemSoundsSession", a.IsSystemSoundsSession, b.IsSystemSoundsSession);
        Cmp("EndpointName", a.EndpointName, b.EndpointName);
        return list;
    }

    private SessionSnapshot? LoadSnapshot(string nameOrPath)
    {
        var path = File.Exists(nameOrPath) ? nameOrPath : Path.Combine(_snapshotDir, nameOrPath + ".json");
        if (!File.Exists(path))
        {
            _log.Warn($"Snapshot '{nameOrPath}' not found ({path}).");
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path), _json)
                   ?? throw new JsonException("empty file");
        }
        catch (Exception ex)
        {
            _log.Exception($"Read snapshot {path}", ex);
            return null;
        }
    }

    // ---------------------------------------------------------------- setvol / mute (the only writes)

    private void SetVolume(int index, int percent) =>
        WithTarget(index, "setvol", (s, label) =>
        {
            var before = s.SimpleAudioVolume.Volume;
            s.SimpleAudioVolume.Volume = percent / 100f;
            var after = s.SimpleAudioVolume.Volume;
            _log.Ok($"setvol [{index}] {label}: {Percent(before)} -> {Percent(after)} (read back)");
        });

    private void SetMute(int index, bool mute) =>
        WithTarget(index, "mute", (s, label) =>
        {
            var before = s.SimpleAudioVolume.Mute;
            s.SimpleAudioVolume.Mute = mute;
            var after = s.SimpleAudioVolume.Mute;
            _log.Ok($"mute [{index}] {label}: {before} -> {after} (read back)");
        });

    /// <summary>
    /// Re-finds the session the user picked by endpoint + raw instance identifier and runs
    /// <paramref name="action"/> on it only if exactly one session matches. Never touches others.
    /// </summary>
    private void WithTarget(int index, string command, Action<AudioSessionControl, string> action)
    {
        SessionTarget? target;
        lock (_targetsLock) target = _targets.FirstOrDefault(t => t.Index == index);
        if (target is null)
        {
            _log.Warn($"No session [{index}] in the last listing. Run 'sessions' or 'sessions all' first.");
            return;
        }

        using var en = new MMDeviceEnumerator();
        MMDevice dev;
        try { dev = en.GetDevice(target.EndpointId); }
        catch (Exception ex)
        {
            _log.Exception($"{command}: endpoint of session [{index}] is gone", ex);
            return;
        }

        using (dev)
        {
            SessionCollection collection;
            try
            {
                var manager = dev.AudioSessionManager;
                manager.RefreshSessions();
                collection = manager.Sessions;
            }
            catch (Exception ex)
            {
                _log.Exception($"{command}: session enumeration", ex);
                return;
            }

            AudioSessionControl? match = null;
            var matches = 0;
            try
            {
                for (var i = 0; i < collection.Count; i++)
                {
                    var s = collection[i];
                    string? id = null;
                    try { id = s.GetSessionInstanceIdentifier; } catch { /* unreadable -> not a match */ }

                    if (id == target.RawInstanceId)
                    {
                        matches++;
                        if (match is null) { match = s; continue; }
                    }
                    s.Dispose();
                }

                if (matches != 1 || match is null)
                {
                    _log.Error($"{command}: expected exactly 1 session matching [{index}], found {matches}. " +
                               "The session may have ended. Nothing was changed. Run 'sessions all' again.");
                    return;
                }

                try { action(match, target.Label); }
                catch (Exception ex) { _log.Exception($"{command} [{index}]", ex); }
            }
            finally
            {
                match?.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Defaults(string? Console, string? Multimedia, string? Communications);

    private Defaults ReadDefaults(MMDeviceEnumerator en)
    {
        string? Get(Role role)
        {
            try
            {
                if (!en.TryGetDefaultAudioEndpoint(DataFlow.Render, role, out var d) || d is null) return null;
                using (d) return d.ID;
            }
            catch (Exception ex)
            {
                _log.Exception($"Default render endpoint ({role})", ex);
                return null;
            }
        }
        return new Defaults(Get(Role.Console), Get(Role.Multimedia), Get(Role.Communications));
    }

    private void LogDefaults(Defaults d)
    {
        _log.Info($"Default render: Console={Redact.Addresses(d.Console) ?? "(none)"}");
        _log.Info($"                Multimedia={Redact.Addresses(d.Multimedia) ?? "(none)"}");
        _log.Info($"                Communications={Redact.Addresses(d.Communications) ?? "(none)"}");
    }

    private EndpointInfo ReadEndpoint(MMDevice dev, Defaults defaults)
    {
        var id = RawEndpointId(dev);
        string name;
        try { name = dev.FriendlyName; }
        catch (Exception ex) { name = $"(FriendlyName unreadable: 0x{ex.HResult:X8})"; }
        string state;
        try { state = dev.State.ToString(); }
        catch (Exception ex) { state = $"(unreadable: 0x{ex.HResult:X8})"; }

        return new EndpointInfo(
            Redact.Addresses(id)!, Redact.Addresses(name)!, state,
            id == defaults.Console, id == defaults.Multimedia, id == defaults.Communications);
    }

    private static string RawEndpointId(MMDevice dev)
    {
        try { return dev.ID; }
        catch (Exception ex) { return $"(ID unreadable: 0x{ex.HResult:X8})"; }
    }

    private static string DefaultTag(EndpointInfo e)
    {
        var roles = new List<string>();
        if (e.IsDefaultConsole) roles.Add("Console");
        if (e.IsDefaultMultimedia) roles.Add("Multimedia");
        if (e.IsDefaultCommunications) roles.Add("Communications");
        return roles.Count == 0 ? "" : $" [DEFAULT: {string.Join(", ", roles)}]";
    }

    private void SetTargets(List<SessionTarget> targets)
    {
        lock (_targetsLock) _targets = targets;
    }

    private static string ProcessName(uint pid)
    {
        if (pid == 0) return "(pid 0: none / cross-process)";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (ArgumentException) { return "(process exited)"; }
        catch (Exception ex) { return $"(access error {ex.GetType().Name} 0x{ex.HResult:X8})"; }
    }

    private static string StateName(AudioSessionState s) => s switch
    {
        AudioSessionState.AudioSessionStateActive => "Active",
        AudioSessionState.AudioSessionStateInactive => "Inactive",
        AudioSessionState.AudioSessionStateExpired => "Expired",
        _ => s.ToString(),
    };

    private static T? ReadV<T>(Func<T> get, string field, List<string> errors) where T : struct
    {
        try { return get(); }
        catch (Exception ex) { errors.Add($"{field}: {ex.GetType().Name} 0x{ex.HResult:X8}"); return null; }
    }

    private static T? ReadR<T>(Func<T> get, string field, List<string> errors) where T : class
    {
        try { return get(); }
        catch (Exception ex) { errors.Add($"{field}: {ex.GetType().Name} 0x{ex.HResult:X8}"); return null; }
    }

    private static string Percent(float? v) => v is { } x ? $"{x * 100:0}%" : "?";
    private static string Show(bool? b) => b?.ToString() ?? "?";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SnapshotName();
}
