using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vydrix.Core;

namespace Vydrix.Games;

/// <summary>
/// Session Vault: Crash-safe game-session accounting.
/// Journalizes active game sessions to disk, validates process identity against Windows PID reuse,
/// survives forced terminations, and recovers cleanly on startup without data loss.
/// </summary>
public sealed class SessionVault
{
    private readonly string _journalPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, VaultSessionRecord> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public SessionVault(string? journalPath = null)
    {
        _journalPath = journalPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vydrix",
            "vault_sessions.json");

        LoadJournal();
    }

    public IReadOnlyList<VaultSessionRecord> ActiveSessions
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Values.Where(s => s.State == "Running").ToList();
            }
        }
    }

    /// <summary>
    /// Records a newly launched game session into the persistent journal.
    /// </summary>
    public VaultSessionRecord RecordLaunch(
        string gameId,
        Process process,
        string mountId,
        IReadOnlyList<string>? pinnedFiles = null)
    {
        DateTime startTime;
        try { startTime = process.StartTime; }
        catch { startTime = DateTime.Now; }

        var record = new VaultSessionRecord(
            Guid.NewGuid().ToString("N")[..12],
            gameId,
            process.Id,
            startTime,
            mountId,
            DateTime.Now,
            null,
            "Running",
            pinnedFiles ?? Array.Empty<string>());

        lock (_lock)
        {
            _sessions[record.SessionId] = record;
            SaveJournal();
        }

        return record;
    }

    /// <summary>
    /// Updates the session state to exited upon normal completion.
    /// </summary>
    public bool MarkExited(string sessionId, string finalState = "Exited")
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                var updated = existing with
                {
                    Ended = DateTime.Now,
                    State = finalState
                };
                _sessions[sessionId] = updated;
                SaveJournal();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Verifies if a recorded process is genuinely still alive without PID reuse deception.
    /// </summary>
    public static bool IsProcessGenuinelyAlive(int pid, DateTime expectedStartTime)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return false;

            // PID Reuse check: Compare start time within 2-second tolerance
            var actualStart = proc.StartTime;
            return Math.Abs((actualStart - expectedStartTime).TotalSeconds) < 2.0;
        }
        catch
        {
            // Process does not exist or access denied -> process is dead
            return false;
        }
    }

    /// <summary>
    /// Reconciles sessions upon VYDRIX startup after a reboot, crash, or unexpected termination.
    /// </summary>
    public async Task<VaultRecoveryReport> ReconcileStartupAsync(Func<string, Task>? cleanupGameCache = null)
    {
        var deadlocksCleared = 0;
        var recoveredCount = 0;
        var details = new List<string>();

        List<VaultSessionRecord> orphaned;
        lock (_lock)
        {
            orphaned = _sessions.Values
                .Where(s => s.State == "Running")
                .ToList();
        }

        foreach (var session in orphaned)
        {
            bool alive = IsProcessGenuinelyAlive(session.ProcessId, session.ProcessStartTime);
            if (!alive)
            {
                recoveredCount++;
                deadlocksCleared++;
                details.Add($"Recovered crashed session {session.SessionId} for game '{session.GameId}' (PID {session.ProcessId} terminated).");

                MarkExited(session.SessionId, "RecoveredAfterCrash");

                if (cleanupGameCache != null)
                {
                    try
                    {
                        await cleanupGameCache(session.GameId);
                        details.Add($"Cleaned orphaned transient cache for '{session.GameId}'.");
                    }
                    catch (Exception ex)
                    {
                        details.Add($"Could not clean cache for '{session.GameId}': {ex.Message}");
                    }
                }
            }
            else
            {
                details.Add($"Process {session.ProcessId} for game '{session.GameId}' is genuinely still running.");
            }
        }

        return new VaultRecoveryReport(recoveredCount, deadlocksCleared, 0, details);
    }

    private void LoadJournal()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_journalPath))
                {
                    var json = File.ReadAllText(_journalPath);
                    var list = JsonSerializer.Deserialize<List<VaultSessionRecord>>(json);
                    if (list != null)
                    {
                        foreach (var s in list)
                        {
                            _sessions[s.SessionId] = s;
                        }
                    }
                }
            }
            catch
            {
                // Non-fatal journal read error
            }
        }
    }

    private void SaveJournal()
    {
        try
        {
            var dir = Path.GetDirectoryName(_journalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Keep at most 100 historical sessions
            var list = _sessions.Values
                .OrderByDescending(s => s.Started)
                .Take(100)
                .ToList();

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_journalPath, json);
        }
        catch
        {
            // Non-fatal journal write error
        }
    }
}
