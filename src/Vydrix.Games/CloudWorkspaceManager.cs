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
/// Manages Cloud Project Workspaces: enables development projects (Python, Node.js, .NET, Flutter, Git, AI)
/// to reside on cloud-backed storage while routing compiler scratch, build output, and package caches
/// to secondary physical storage (or bounded RAM) to shield the C: drive.
/// </summary>
public sealed class CloudWorkspaceManager
{
    private readonly string _stateFilePath;
    private readonly CloudBrokerSettings _settings;
    private readonly ConcurrentDictionary<string, CloudWorkspaceDefinition> _workspaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fileLock = new();

    public CloudWorkspaceManager(string? stateFilePath = null, CloudBrokerSettings? settings = null)
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vydrix");
        _stateFilePath = stateFilePath ?? Path.Combine(dataRoot, "workspaces.json");
        _settings = settings ?? new CloudBrokerSettings();

        LoadState();
    }

    public IReadOnlyList<CloudWorkspaceDefinition> GetWorkspaces()
    {
        return _workspaces.Values.OrderByDescending(w => w.LastAccessed).ToList();
    }

    public CloudWorkspaceDefinition? GetWorkspace(string workspaceId)
    {
        _workspaces.TryGetValue(workspaceId, out var def);
        return def;
    }

    /// <summary>
    /// Creates a new cloud workspace with template files and configured scratch directories.
    /// </summary>
    public CloudWorkspaceDefinition CreateWorkspace(
        string name,
        CloudWorkspaceTemplate template,
        string? cloudRoot = null,
        string? scratchRoot = null)
    {
        var sanitizedName = SanitizeName(name);
        var workspaceId = $"ws_{sanitizedName}_{Guid.NewGuid():N}"[..18];

        string cloudBase = cloudRoot ?? _settings.WorkspacesRootPath;
        string cloudPath = Path.Combine(cloudBase, sanitizedName);

        // Determine scratch location (D:\VydrixScratch or custom)
        string localScratchBase = scratchRoot ?? _settings.CustomScratchPath ?? FindSecondaryScratchRoot();
        string localScratchPath = Path.Combine(localScratchBase, sanitizedName);
        string buildOutputDir = Path.Combine(localScratchPath, "build");

        Directory.CreateDirectory(cloudPath);
        Directory.CreateDirectory(localScratchPath);
        Directory.CreateDirectory(buildOutputDir);

        // Seed project template
        ApplyTemplate(cloudPath, localScratchPath, template, sanitizedName);

        var now = DateTime.Now;
        var def = new CloudWorkspaceDefinition(
            workspaceId,
            sanitizedName,
            template,
            cloudPath,
            localScratchPath,
            buildOutputDir,
            now,
            now);

        _workspaces[workspaceId] = def;
        SaveState();
        return def;
    }

    /// <summary>
    /// Verifies workspace health: checks write access, file locking, latency, and watcher support.
    /// </summary>
    public async Task<WorkspaceHealthReport> VerifyWorkspaceHealthAsync(string workspaceId)
    {
        if (!_workspaces.TryGetValue(workspaceId, out var def))
        {
            return new WorkspaceHealthReport(
                workspaceId,
                IsAccessible: false,
                LockTestPassed: false,
                WatcherSupported: false,
                LatencyMs: 0.0,
                Warnings: ["Workspace definition not found."]);
        }

        var warnings = new List<string>();
        bool isAccessible = Directory.Exists(def.CloudPath);
        bool lockTestPassed = false;
        bool watcherSupported = false;
        double latencyMs = 0.0;

        if (!isAccessible)
        {
            warnings.Add($"Cloud path '{def.CloudPath}' is not currently accessible. Ensure virtual drive is mounted.");
            return new WorkspaceHealthReport(workspaceId, false, false, false, 0.0, warnings);
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var testFile = Path.Combine(def.CloudPath, $".vydrix_health_{Guid.NewGuid():N}.tmp");

            // Test 1: Write and lock
            await using (var fs = new FileStream(testFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                byte[] data = [0x56, 0x59, 0x44, 0x52, 0x49, 0x58];
                await fs.WriteAsync(data);
                await fs.FlushAsync();

                // Test exclusive lock behavior
                try
                {
                    using var secondFs = new FileStream(testFile, FileMode.Open, FileAccess.Read, FileShare.None);
                    lockTestPassed = false;
                }
                catch (IOException)
                {
                    lockTestPassed = true; // Expected: lock succeeded
                }
            }

            // Test 2: Watcher support
            try
            {
                using var watcher = new FileSystemWatcher(def.CloudPath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                watcherSupported = true;
            }
            catch (Exception ex)
            {
                watcherSupported = false;
                warnings.Add($"FileSystemWatcher warning: {ex.Message}. IDE file auto-reload may require manual refresh.");
            }

            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Workspace access test error: {ex.Message}");
        }

        sw.Stop();
        latencyMs = sw.Elapsed.TotalMilliseconds;

        if (latencyMs > 50.0)
        {
            warnings.Add($"Elevated filesystem latency ({latencyMs:F1} ms). Recommended: Keep compiler scratch on physical storage.");
        }

        return new WorkspaceHealthReport(
            workspaceId,
            isAccessible,
            lockTestPassed,
            watcherSupported,
            latencyMs,
            warnings);
    }

    /// <summary>
    /// Measures disk consumption of the workspace across cloud and scratch volumes.
    /// </summary>
    public (long CloudBytes, long ScratchBytes) MeasureWorkspaceFootprint(string workspaceId)
    {
        if (!_workspaces.TryGetValue(workspaceId, out var def)) return (0, 0);

        long cloudBytes = MeasureDir(def.CloudPath);
        long scratchBytes = MeasureDir(def.LocalScratchPath);
        return (cloudBytes, scratchBytes);
    }

    private void ApplyTemplate(string cloudPath, string scratchPath, CloudWorkspaceTemplate template, string name)
    {
        switch (template)
        {
            case CloudWorkspaceTemplate.Python:
                File.WriteAllText(Path.Combine(cloudPath, "README.md"), $"# {name}\n\nPython Cloud Workspace backed by VYDRIX.\n");
                File.WriteAllText(Path.Combine(cloudPath, ".gitignore"),
                    ".venv/\n__pycache__/\n*.pyc\n*.pyo\n.pytest_cache/\n.coverage\nbuild/\ndist/\n");
                File.WriteAllText(Path.Combine(cloudPath, "requirements.txt"), "# Add your pip requirements here\n");
                File.WriteAllText(Path.Combine(cloudPath, "main.py"), "def main():\n    print('Hello from VYDRIX Cloud Workspace!')\n\nif __name__ == '__main__':\n    main()\n");
                File.WriteAllText(Path.Combine(cloudPath, ".env"), $"PIP_CACHE_DIR={Path.Combine(scratchPath, "pip-cache")}\n");
                break;

            case CloudWorkspaceTemplate.NodeJs:
                File.WriteAllText(Path.Combine(cloudPath, "README.md"), $"# {name}\n\nNode.js Cloud Workspace backed by VYDRIX.\n");
                File.WriteAllText(Path.Combine(cloudPath, "package.json"),
                    $"{{\n  \"name\": \"{name.ToLowerInvariant()}\",\n  \"version\": \"1.0.0\",\n  \"main\": \"index.js\",\n  \"scripts\": {{\n    \"start\": \"node index.js\"\n  }}\n}}\n");
                File.WriteAllText(Path.Combine(cloudPath, ".npmrc"), $"cache={Path.Combine(scratchPath, "npm-cache")}\n");
                File.WriteAllText(Path.Combine(cloudPath, ".gitignore"), "node_modules/\ndist/\n.npm/\n*.log\n");
                File.WriteAllText(Path.Combine(cloudPath, "index.js"), "console.log('Hello from VYDRIX Node.js Workspace!');\n");
                break;

            case CloudWorkspaceTemplate.DotNet:
                File.WriteAllText(Path.Combine(cloudPath, "README.md"), $"# {name}\n\n.NET Cloud Workspace backed by VYDRIX.\n");
                File.WriteAllText(Path.Combine(cloudPath, "Directory.Build.props"),
                    $"<Project>\n  <PropertyGroup>\n    <!-- Redirect compiler intermediate and output artifacts off C: and cloud -->\n    <BaseIntermediateOutputPath>{Path.Combine(scratchPath, "obj")}\\</BaseIntermediateOutputPath>\n    <BaseOutputPath>{Path.Combine(scratchPath, "bin")}\\</BaseOutputPath>\n  </PropertyGroup>\n</Project>\n");
                File.WriteAllText(Path.Combine(cloudPath, ".gitignore"), "bin/\nobj/\n*.user\n.vs/\n");
                File.WriteAllText(Path.Combine(cloudPath, "Program.cs"), "Console.WriteLine(\"Hello from VYDRIX .NET Workspace!\");\n");
                break;

            case CloudWorkspaceTemplate.Flutter:
                File.WriteAllText(Path.Combine(cloudPath, "README.md"), $"# {name}\n\nFlutter Cloud Workspace backed by VYDRIX.\n");
                File.WriteAllText(Path.Combine(cloudPath, "pubspec.yaml"),
                    $"name: {name.ToLowerInvariant()}\ndescription: A new Flutter project created with VYDRIX.\nversion: 1.0.0+1\nenvironment:\n  sdk: '>=3.0.0 <4.0.0'\n");
                File.WriteAllText(Path.Combine(cloudPath, ".gitignore"), ".dart_tool/\nbuild/\n.flutter-plugins\n.flutter-plugins-dependencies\n");
                break;

            case CloudWorkspaceTemplate.AiDataset:
                File.WriteAllText(Path.Combine(cloudPath, "README.md"), $"# {name} Dataset\n\nAI Dataset & Training Manifest backed by VYDRIX.\n");
                File.WriteAllText(Path.Combine(cloudPath, "dataset_manifest.json"),
                    $"{{\n  \"datasetName\": \"{name}\",\n  \"created\": \"{DateTime.UtcNow:O}\",\n  \"splits\": [\"train\", \"validation\", \"test\"],\n  \"recordsCount\": 0\n}}\n");
                File.WriteAllText(Path.Combine(cloudPath, ".gitignore"), "*.cache\n*.tmp\nscratch/\n");
                break;

            default:
                File.WriteAllText(Path.Combine(cloudPath, "README.md"), $"# {name}\n\nGit Workspace backed by VYDRIX.\n");
                File.WriteAllText(Path.Combine(cloudPath, ".gitignore"), "*.tmp\n*.bak\n.cache/\n");
                break;
        }
    }

    private static string FindSecondaryScratchRoot()
    {
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                if (d.DriveType != DriveType.Fixed) continue;
                if (d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)) continue;
                if (d.Name.StartsWith("G", StringComparison.OrdinalIgnoreCase)) continue;

                if (d.AvailableFreeSpace > 5L * ByteSize.GiB)
                {
                    return Path.Combine(d.Name, "VydrixScratch");
                }
            }
        }
        catch { }

        return Path.Combine(Path.GetTempPath(), "VydrixScratch");
    }

    private static long MeasureDir(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    private void LoadState()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_stateFilePath)) return;
            try
            {
                var json = File.ReadAllText(_stateFilePath);
                var items = JsonSerializer.Deserialize<List<CloudWorkspaceDefinition>>(json);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        _workspaces[item.WorkspaceId] = item;
                    }
                }
            }
            catch { }
        }
    }

    private void SaveState()
    {
        lock (_fileLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_stateFilePath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_workspaces.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_stateFilePath, json);
            }
            catch { }
        }
    }

    private static string SanitizeName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray();
        return new string(chars).Trim('_');
    }
}
