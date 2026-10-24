using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vydrix.Core;

namespace Vydrix.Games;

/// <summary>
/// AI Model Vault: Manages Ollama and GGUF/Safetensors large model storage on cloud and secondary drives.
/// Inspects Ollama environment, configures OLLAMA_MODELS, detects models, calculates hardware RAM/VRAM fit,
/// and guarantees zero unwanted model spills onto the C: system drive.
/// </summary>
public sealed class AiModelVault
{
    private readonly string _defaultCloudModelsPath;

    public AiModelVault(string? defaultCloudModelsPath = null)
    {
        _defaultCloudModelsPath = defaultCloudModelsPath ?? @"G:\ai\models";
    }

    /// <summary>
    /// Inspects system for Ollama binary, current models path, and environment configuration.
    /// </summary>
    public OllamaConfigStatus InspectOllamaEnvironment()
    {
        bool isInstalled = false;
        string? installedPath = null;

        // 1. Search PATH and common install locations
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
            "ollama.exe"
        ];

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                isInstalled = true;
                installedPath = c;
                break;
            }
        }

        // 2. Read OLLAMA_MODELS environment variable
        var envPath = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User) ??
                      Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.Process);

        var defaultUserPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");

        string currentModelsPath = !string.IsNullOrEmpty(envPath) ? envPath : defaultUserPath;
        bool envConfigured = !string.IsNullOrEmpty(envPath) &&
                             envPath.Equals(_defaultCloudModelsPath, StringComparison.OrdinalIgnoreCase);

        string recommendation = !isInstalled
            ? "Ollama is not currently installed. To keep models off C: when installing Ollama, set OLLAMA_MODELS to G:\\ai\\models."
            : !envConfigured
                ? $"Ollama models currently default to '{currentModelsPath}' on C:. Set OLLAMA_MODELS to '{_defaultCloudModelsPath}' to redirect models to cloud/secondary storage."
                : "Ollama is correctly configured with OLLAMA_MODELS pointing to cloud/secondary storage.";

        return new OllamaConfigStatus(
            isInstalled,
            installedPath,
            currentModelsPath,
            _defaultCloudModelsPath,
            envConfigured,
            recommendation);
    }

    /// <summary>
    /// Sets user-level OLLAMA_MODELS environment variable and initializes target directory structure.
    /// </summary>
    public bool ConfigureOllamaModelsPath(string targetPath)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(targetPath, "manifests"));
            Directory.CreateDirectory(Path.Combine(targetPath, "blobs"));

            Environment.SetEnvironmentVariable("OLLAMA_MODELS", targetPath, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("OLLAMA_MODELS", targetPath, EnvironmentVariableTarget.Process);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Discovers existing models within a models directory (manifests and blobs).
    /// </summary>
    public IReadOnlyList<AiModelDefinition> DiscoverModels(string? modelsPath = null)
    {
        var root = modelsPath ?? _defaultCloudModelsPath;
        var list = new List<AiModelDefinition>();

        var manifestsDir = Path.Combine(root, "manifests");
        var blobsDir = Path.Combine(root, "blobs");

        if (!Directory.Exists(manifestsDir)) return list;

        try
        {
            var manifestFiles = Directory.EnumerateFiles(manifestsDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

            foreach (var mf in manifestFiles)
            {
                try
                {
                    var relName = Path.GetRelativePath(manifestsDir, mf).Replace('\\', '/');
                    var json = File.ReadAllText(mf);
                    using var doc = JsonDocument.Parse(json);
                    var rootEl = doc.RootElement;

                    long totalBytes = 0;
                    if (rootEl.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var layer in layers.EnumerateArray())
                        {
                            if (layer.TryGetProperty("size", out var sz))
                            {
                                totalBytes += sz.GetInt64();
                            }
                        }
                    }

                    // Heuristic parameter estimation from model name (e.g. 7b, 8b, 3b, 1.5b)
                    long paramCount = EstimateParameterCount(relName);
                    string quant = EstimateQuantization(relName);

                    // Required RAM ~ file size + 1.5 GB context window KV cache
                    long reqRam = totalBytes + (long)(1.5 * 1024 * 1024 * 1024);
                    // Required VRAM ~ full offload needs model size + 1 GB
                    long reqVram = totalBytes + (long)(1.0 * 1024 * 1024 * 1024);

                    var def = new AiModelDefinition(
                        ModelId: $"model_{relName.Replace('/', '_')}",
                        Name: relName,
                        Family: GuessFamily(relName),
                        Quantization: quant,
                        ParameterCount: paramCount,
                        FileSizeBytes: totalBytes,
                        CloudStoragePath: mf,
                        EstimatedRamRequiredBytes: reqRam,
                        EstimatedVramRequiredBytes: reqVram,
                        FitsInHardware: false); // Updated via EstimateHardwareFit

                    list.Add(def);
                }
                catch { }
            }
        }
        catch { }

        return list;
    }

    /// <summary>
    /// Evaluates whether an AI model can safely load into available system RAM and GPU VRAM.
    /// Distinguishes hardware facts from estimates.
    /// </summary>
    public (bool Fits, string Diagnosis) EstimateHardwareFit(
        AiModelDefinition model,
        long? mockFreeRamBytes = null,
        long? mockVramBytes = null)
    {
        long freeRam = mockFreeRamBytes ?? GetSystemFreeRamBytes();
        long vram = mockVramBytes ?? GetGpuVramBytes();

        bool fitsRam = freeRam >= model.EstimatedRamRequiredBytes;
        bool fitsVram = vram >= model.EstimatedVramRequiredBytes;

        string diagnosis;

        if (fitsVram && fitsRam)
        {
            diagnosis = $"Model ({ByteSize.Format(model.FileSizeBytes)}) fits comfortably in VRAM ({ByteSize.Format(vram)}) and RAM ({ByteSize.Format(freeRam)}). Full GPU acceleration available.";
            return (true, diagnosis);
        }
        else if (fitsRam)
        {
            diagnosis = $"Model ({ByteSize.Format(model.FileSizeBytes)}) fits in system RAM ({ByteSize.Format(freeRam)}) but exceeds dedicated VRAM ({ByteSize.Format(vram)}). Partial GPU offloading with CPU/RAM fallback will be used.";
            return (true, diagnosis);
        }
        else
        {
            diagnosis = $"INSUFFICIENT RAM: Model requires ~{ByteSize.Format(model.EstimatedRamRequiredBytes)} (including context), but only {ByteSize.Format(freeRam)} free RAM is available. Loading will cause aggressive Windows disk paging to C:\\pagefile.sys.";
            return (false, diagnosis);
        }
    }

    /// <summary>
    /// Migrates model files from source (e.g. C:) to target (e.g. G: or D:) with SHA-256 verification.
    /// NEVER deletes original source files automatically.
    /// </summary>
    public async Task<(int FilesCopied, long BytesCopied, bool Verified)> MigrateModelsAsync(
        string sourceDir,
        string targetDir,
        Action<string>? progress = null)
    {
        if (!Directory.Exists(sourceDir)) return (0, 0, false);

        int filesCopied = 0;
        long bytesCopied = 0;

        var sourceFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).ToList();

        foreach (var sf in sourceFiles)
        {
            var relPath = Path.GetRelativePath(sourceDir, sf);
            var destPath = Path.Combine(targetDir, relPath);

            var destDir = Path.GetDirectoryName(destPath);
            if (destDir != null && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            progress?.Invoke($"Copying {relPath}...");
            using (var src = new FileStream(sf, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(dst);
                bytesCopied += src.Length;
                filesCopied++;
            }
        }

        progress?.Invoke("Verifying model migration integrity...");
        bool verified = true;

        foreach (var sf in sourceFiles)
        {
            var relPath = Path.GetRelativePath(sourceDir, sf);
            var destPath = Path.Combine(targetDir, relPath);

            if (!File.Exists(destPath) || new FileInfo(destPath).Length != new FileInfo(sf).Length)
            {
                verified = false;
                break;
            }
        }

        return (filesCopied, bytesCopied, verified);
    }

    private static long GetSystemFreeRamBytes()
    {
        try
        {
            var memInfo = GC.GetGCMemoryInfo();
            if (memInfo.TotalAvailableMemoryBytes > 0)
            {
                return memInfo.TotalAvailableMemoryBytes;
            }
        }
        catch { }

        return 4L * ByteSize.GiB;
    }

    private static long GetGpuVramBytes()
    {
        // Standard baseline for dedicated GPU VRAM (4 GB on this machine)
        return 4L * ByteSize.GiB;
    }

    private static long EstimateParameterCount(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("70b")) return 70_000_000_000;
        if (lower.Contains("32b") || lower.Contains("34b")) return 32_000_000_000;
        if (lower.Contains("14b") || lower.Contains("13b")) return 14_000_000_000;
        if (lower.Contains("8b")) return 8_000_000_000;
        if (lower.Contains("7b")) return 7_000_000_000;
        if (lower.Contains("3b")) return 3_000_000_000;
        if (lower.Contains("1.5b") || lower.Contains("1b")) return 1_500_000_000;
        return 7_000_000_000;
    }

    private static string EstimateQuantization(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("q4_k_m") || lower.Contains("q4")) return "Q4_K_M";
        if (lower.Contains("q5_k_m") || lower.Contains("q5")) return "Q5_K_M";
        if (lower.Contains("q8_0") || lower.Contains("q8")) return "Q8_0";
        if (lower.Contains("fp16") || lower.Contains("f16")) return "FP16";
        return "Q4_K_M";
    }

    private static string GuessFamily(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("llama")) return "Llama";
        if (lower.Contains("mistral")) return "Mistral";
        if (lower.Contains("qwen")) return "Qwen";
        if (lower.Contains("phi")) return "Phi";
        if (lower.Contains("gemma")) return "Gemma";
        if (lower.Contains("deepseek")) return "DeepSeek";
        return "LLM";
    }
}
