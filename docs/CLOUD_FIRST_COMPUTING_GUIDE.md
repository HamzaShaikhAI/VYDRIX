# VYDRIX 3.0 — Cloud-First Computing Operator Guide
## Running Local Workloads from Cloud Storage While Shielding the System Drive

---

## 1. Overview

VYDRIX 3.0 introduces **Cloud-First Computing**: a unified architecture that enables you to run demanding applications directly against your cloud-backed virtual drive (`G:`), utilizing your local PC's CPU, GPU, and RAM, while keeping your physical `C:` drive completely shielded from massive asset downloads and compiler thrashing.

---

## 2. Setting Up Cloud Project Workspaces

Cloud Workspaces let you develop and build projects hosted on `G:\workspaces` with build outputs and package caches safely redirected to a secondary drive (or bounded RAM).

### Supported Templates:
1. **Python:** Generates `requirements.txt`, `.gitignore`, `main.py`, and `.env` redirecting `PIP_CACHE_DIR` to local scratch.
2. **Node.js:** Generates `package.json`, `.npmrc` setting `cache=<scratch>/npm-cache`, and `.gitignore`.
3. **.NET:** Generates `Directory.Build.props` redirecting `<BaseIntermediateOutputPath>` (`obj/`) and `<BaseOutputPath>` (`bin/`) off `C:` and the cloud drive to fast local scratch.
4. **Flutter:** Generates `pubspec.yaml`, `l10n.yaml`, and `.gitignore` ignoring `.dart_tool` and `build/`.
5. **Git Repositories:** Standard `.gitignore` and `.gitattributes`.
6. **AI Datasets:** `dataset_manifest.json` and training partition manifests.

### CLI Commands:
```powershell
# List existing cloud workspaces
vydrix.exe workspace list

# Create a new .NET workspace
vydrix.exe workspace create MyWebApi --template dotnet

# Create a Python ML workspace
vydrix.exe workspace create ModelTrainer --template python

# Verify workspace health, locking capability, and latency
vydrix.exe workspace check ws_MyWebApi_a1b2c3
```

---

## 3. AI Model Vault for Ollama

Large Language Models (e.g. Llama 3.2, Qwen 2.5, Mistral) range from 2 GB to 40+ GB in size. Storing these models on `C:` quickly exhausts SSD space.

### How Ollama Operates:
- By default on Windows, Ollama stores all downloaded models in `%USERPROFILE%\.ollama\models` on `C:`.
- Ollama memory-maps (`mmap`) model blobs from disk during inference.

### Relocating Ollama Models to Cloud Storage:
1. **Check Status:**
   ```powershell
   vydrix.exe ollama status
   ```
2. **Configure Cloud Storage Path:**
   ```powershell
   vydrix.exe ollama config --path "G:\ai\models"
   ```
   This command:
   - Sets the user-level environment variable `OLLAMA_MODELS=G:\ai\models`.
   - Initializes the required `manifests/` and `blobs/` directory structures on `G:`.
   - Protects your `C:` drive from any future `ollama pull` or `ollama run` downloads!

### Hardware Fit & Sizing Rules:
- **7B / 8B Parameter Models (Q4_K_M):** File size ~4.4 GB. Requires ~5.5 GB RAM (including context KV cache) and ~4.5 GB VRAM for full GPU offload.
- **3B Parameter Models:** File size ~2.0 GB. Fits comfortably in 4 GB VRAM and 8 GB RAM.
- **Preflight Evaluation:**
  ```powershell
  vydrix.exe preflight ai "G:\ai\models\manifests\registry.ollama.ai\library\llama3.2\3b"
  ```

---

## 4. Game Vault: AAA Cloud Streaming

### Game Compatibility Triage:
- **Single-Player / DRM-Free / Standard Steam Titles:** **100% Compatible.** Assets stream on-demand into the 512 MiB Zero Footprint cache.
- **Kernel Anti-Cheat Titles (Riot Vanguard, Easy Anti-Cheat, BattlEye):**
  - Kernel-level drivers actively inspect filesystem filter drivers and may flag WinFsp user-mode filesystems as untrusted or trigger anti-tamper errors (e.g., EAC 10011).
  - VYDRIX's Game Vault flags these titles during preflight checks so you are never surprised.

### Save Game Protection:
- Save games are automatically discovered in `%USERPROFILE%\Saved Games`, `%LOCALAPPDATA%`, or Steam Cloud.
- VYDRIX **never** includes save-game directories in cache reclamation or emergency cleanup routines.

---

## 5. Workload Preflight Checker

Before launching any heavy workload, execute a preflight check:

```powershell
vydrix.exe preflight game "G:\games\Cyberpunk2077" --profile StrictCloud
vydrix.exe preflight ai "G:\ai\models\qwen2.5-7b.gguf" --profile AiInference
vydrix.exe preflight workspace "G:\workspaces\MyApi" --profile Development
```

### Preflight Output Details:
- **Hardware Resources:** Evaluates available RAM and dedicated GPU VRAM against required workload working sets.
- **C: Capacity:** Verifies that C: has sufficient headroom to absorb unavoidable system paging.
- **Placement Policy:** Reports where cache and scratch data will land (e.g. `D:\VydrixCache` or RAM).
- **Network Health:** Alerts if cloud RTT exceeds 250 ms.
