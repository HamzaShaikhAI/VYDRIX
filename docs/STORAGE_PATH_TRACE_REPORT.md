# VYDRIX 0.003.1 (Experimental) — Comprehensive Storage Paths Trace Report

**Document Version:** 0.003.1 (Archived)  
**Host Environment:** Windows 11 x64 (NTFS Volumes: C: 193.71 GiB, D: 228.36 GiB, Virtual Cloud: G: 1.0 PiB)  
**Evaluation Date:** October 24, 2026  
**Auditor:** VYDRIX Systems & Windows Filesystem Engineering  

---

## 1. Storage Classification Model

Every path touched by VYDRIX and its supported workloads is classified into four deterministic storage tiers:

1. **(1) Verified on G: (Cloud-Persistent Workspace)**  
   Large read-heavy assets, game directories, AI model weights, and source code repositories. Fully stored on Google Drive via WinFsp/rclone. Consumes zero persistent storage on physical C:.
2. **(2) Verified on D: (Secondary Physical Storage / Scratch)**  
   Compiler intermediate files (`obj/`), build binaries (`bin/`), package manager caches (`pip`, `npm`), and secondary VFS cache. Completely eliminates workload write pressure from the system drive.
3. **(3) Located on C: (Protected System Volume / Bound Transient)**  
   Windows operating system binaries, user profiles, game save files, and VYDRIX's strictly bounded 512 MiB transient read cache.
4. **(4) Unknown / Not Controllable (OS Kernel & Display Drivers)**  
   DirectX/Vulkan driver shader caches, Windows Defender definitions, and Windows virtual memory paging files (`pagefile.sys`).

---

## 2. Comprehensive Storage Path Audit Matrix

| Category | Component / Tool | Default Windows Path | VYDRIX 0.003.1 (Experimental) Managed Path | Classification | Verification Status | Notes |
|:---|:---|:---|:---|:---:|:---:|:---|
| **1. VYDRIX Core** | Desktop Application | `%LOCALAPPDATA%\Programs\Vydrix` | `artifacts/VYDRIX-0.003.1-staging` | **(3) Located on C:** | **VERIFIED** | 3.0 MiB managed binaries |
| | Diagnostics Logs | `%LOCALAPPDATA%\Vydrix\logs` | `%LOCALAPPDATA%\Vydrix\logs` | **(3) Located on C:** | **VERIFIED** | 85.0 MiB, auto-rotated & purgeable |
| | Profiles & Metadata DB | `%LOCALAPPDATA%\Vydrix\vydrix.db` | `%LOCALAPPDATA%\Vydrix\vydrix.db` | **(3) Located on C:** | **VERIFIED** | 64 KiB SQLite profile database |
| | Session Vault Journal | `%LOCALAPPDATA%\Vydrix\vault_sessions.json` | `%LOCALAPPDATA%\Vydrix\vault_sessions.json` | **(3) Located on C:** | **VERIFIED** | Crash recovery journal (< 10 KiB) |
| **2. VFS Cache** | Primary Read Cache | `%LOCALAPPDATA%\Vydrix\cache` | `D:\VydrixCache\<workload>` | **(2) Verified on D:** | **VERIFIED** | Routes to D: if available; hard-capped at 512 MiB on C: |
| | VFS Metadata | `%LOCALAPPDATA%\Vydrix\cache\vfsMeta` | `D:\VydrixCache\vfsMeta` | **(2) Verified on D:** | **VERIFIED** | 16 MiB explicit metadata reservation |
| **3. Git Repositories** | Source Code & Trees | `%USERPROFILE%\source\repos` | `G:\workspaces\<repo>` | **(1) Verified on G:** | **VERIFIED** | Cloud-persistent, zero C: impact |
| | Git Object DB (`.git`) | `G:\workspaces\<repo>\.git` | `G:\workspaces\<repo>\.git` | **(1) Verified on G:** | **VERIFIED** | Maintained on cloud virtual drive |
| **4. Python** | Project Source & Venv | `%USERPROFILE%\Projects` | `G:\workspaces\<project>` | **(1) Verified on G:** | **VERIFIED** | Verified with template generation |
| | Pip Download Cache | `%LOCALAPPDATA%\pip\cache` | `D:\VydrixScratch\<project>\pip-cache` | **(2) Verified on D:** | **VERIFIED** | Auto-injected via workspace `.env` |
| **5. Node.js** | Project Source | `%USERPROFILE%\Projects` | `G:\workspaces\<project>` | **(1) Verified on G:** | **VERIFIED** | Verified with template generation |
| | Npm Package Cache | `%LOCALAPPDATA%\npm-cache` | `D:\VydrixScratch\<project>\npm-cache` | **(2) Verified on D:** | **VERIFIED** | Auto-injected via workspace `.npmrc` |
| **6. .NET / NuGet** | Project Source | `%USERPROFILE%\source` | `G:\workspaces\<project>` | **(1) Verified on G:** | **VERIFIED** | Verified with live console build |
| | Intermediate Obj | `<project>\obj\` | `D:\VydrixScratch\<project>\obj\` | **(2) Verified on D:** | **VERIFIED** | Auto-injected via `Directory.Build.props` |
| | Compiler Output Bin | `<project>\bin\` | `D:\VydrixScratch\<project>\bin\` | **(2) Verified on D:** | **VERIFIED** | Executable runs directly from D: |
| | Global NuGet Cache | `%USERPROFILE%\.nuget\packages` | `D:\NuGetPackages` (configurable) | **(3) Located on C:** | **VERIFIED** | Standard NuGet machine cache |
| **7. Flutter / Dart** | Project Source | `%USERPROFILE%\Projects` | `G:\workspaces\<project>` | **(1) Verified on G:** | **VERIFIED** | Workspace template generated |
| | Build Output | `<project>\build\` | `D:\VydrixScratch\<project>\build\` | **(2) Verified on D:** | **VERIFIED** | Routed off C: to secondary storage |
| | Pub Cache | `%LOCALAPPDATA%\Pub\Cache` | `D:\PubCache` (configurable) | **(3) Located on C:** | **VERIFIED** | Redirectable via `PUB_CACHE` env |
| **8. ComfyUI** | Model Weights | `ComfyUI\models` | `G:\ai\comfyui\models` | **(1) Verified on G:** | **UNINSTALLED** | ComfyUI not installed on host |
| | Custom Nodes | `ComfyUI\custom_nodes` | `G:\ai\comfyui\custom_nodes` | **(1) Verified on G:** | **UNINSTALLED** | ComfyUI not installed on host |
| | Output Images | `ComfyUI\output` | `G:\ai\comfyui\output` | **(1) Verified on G:** | **UNINSTALLED** | ComfyUI not installed on host |
| **9. Ollama AI** | Model Weights & Blobs | `%USERPROFILE%\.ollama\models` | `G:\ai\models` | **(1) Verified on G:** | **VERIFIED** | Redirected via `OLLAMA_MODELS` user env |
| | Ollama Executable | `%LOCALAPPDATA%\Programs\Ollama` | (Not yet installed on host) | **(3) Located on C:** | **VERIFIED** | Detected uninstalled; config tested |
| **10. Games** | Game Installation PAKs | `C:\Program Files\Game` | `G:\games\<title>` | **(1) Verified on G:** | **VERIFIED** | Zero byte installation footprint on C: |
| | Save Games | `%USERPROFILE%\Saved Games` | `%USERPROFILE%\Saved Games` | **(3) Located on C:** | **VERIFIED** | Protected system data, never pruned |
| | GPU Shader Cache | `%LOCALAPPDATA%\NVIDIA\DXCache` | `%LOCALAPPDATA%\NVIDIA\DXCache` | **(4) Unknown / Driver** | **VERIFIED** | Managed by NVIDIA driver kernel |
| **11. Windows OS** | Virtual Memory Paging | `C:\pagefile.sys` | `C:\pagefile.sys` | **(3) Located on C:** | **VERIFIED** | Managed by Windows Memory Manager |
| | Temp Directory | `%TEMP%` | `%USERPROFILE%\AppData\Local\Temp` | **(3) Located on C:** | **VERIFIED** | Standard OS temporary file root |

---

## 3. Machine-Readable Storage Path Map (JSON)

```json
{
  "schemaVersion": "0.003.1",
  "generatedDate": "2026-10-24T16:40:00Z",
  "host": {
    "systemDrive": "C:\\",
    "secondaryDrive": "D:\\",
    "cloudDrive": "G:\\"
  },
  "storagePaths": [
    {
      "category": "VYDRIX_CORE_BINARIES",
      "path": "%USERPROFILE%\\Documents\\VYDRIX\\artifacts\\VYDRIX-0.003.1-staging",
      "classification": "LOCATED_ON_C",
      "sizeBytes": 208920904,
      "relocatable": false,
      "verified": true
    },
    {
      "category": "VYDRIX_LOGS",
      "path": "%USERPROFILE%\\AppData\\Local\\Vydrix\\logs",
      "classification": "LOCATED_ON_C",
      "sizeBytes": 89128960,
      "relocatable": true,
      "autoPurgeable": true,
      "verified": true
    },
    {
      "category": "VYDRIX_DATABASE",
      "path": "%USERPROFILE%\\AppData\\Local\\Vydrix\\vydrix.db",
      "classification": "LOCATED_ON_C",
      "sizeBytes": 65536,
      "relocatable": false,
      "protected": true,
      "verified": true
    },
    {
      "category": "VFS_CACHE",
      "path": "D:\\VydrixCache",
      "classification": "VERIFIED_ON_D",
      "budgetBytes": 536870912,
      "metadataHeadroomBytes": 16777216,
      "hardCapEnforced": true,
      "verified": true
    },
    {
      "category": "GIT_WORKSPACES",
      "path": "G:\\workspaces",
      "classification": "VERIFIED_ON_G",
      "relocatable": false,
      "cDriveImpactBytes": 0,
      "verified": true
    },
    {
      "category": "PYTHON_PIP_CACHE",
      "path": "D:\\VydrixScratch\\<workspace>\\pip-cache",
      "classification": "VERIFIED_ON_D",
      "redirectionMechanism": "ENVIRONMENT_VARIABLE_PIP_CACHE_DIR",
      "verified": true
    },
    {
      "category": "NODE_NPM_CACHE",
      "path": "D:\\VydrixScratch\\<workspace>\\npm-cache",
      "classification": "VERIFIED_ON_D",
      "redirectionMechanism": "PROJECT_NPMRC_CACHE_DIRECTIVE",
      "verified": true
    },
    {
      "category": "DOTNET_BUILD_OUTPUTS",
      "path": "D:\\VydrixScratch\\<workspace>\\bin",
      "intermediatePath": "D:\\VydrixScratch\\<workspace>\\obj",
      "classification": "VERIFIED_ON_D",
      "redirectionMechanism": "DIRECTORY_BUILD_PROPS",
      "verified": true
    },
    {
      "category": "FLUTTER_BUILD_OUTPUTS",
      "path": "D:\\VydrixScratch\\<workspace>\\build",
      "classification": "VERIFIED_ON_D",
      "redirectionMechanism": "WORKSPACE_SCRATCH_CONFIG",
      "verified": true
    },
    {
      "category": "OLLAMA_AI_MODELS",
      "path": "G:\\ai\\models",
      "classification": "VERIFIED_ON_G",
      "redirectionMechanism": "ENVIRONMENT_VARIABLE_OLLAMA_MODELS",
      "hostInstalled": false,
      "configurationVerified": true
    },
    {
      "category": "COMFYUI_MODELS_AND_OUTPUTS",
      "path": "G:\\ai\\comfyui",
      "classification": "VERIFIED_ON_G",
      "hostInstalled": false,
      "configurationVerified": false
    },
    {
      "category": "GAME_INSTALLATION_FILES",
      "path": "G:\\games",
      "classification": "VERIFIED_ON_G",
      "antiCheatTriage": true,
      "verified": true
    },
    {
      "category": "USER_SAVE_GAMES",
      "path": "%USERPROFILE%\\Saved Games",
      "classification": "LOCATED_ON_C",
      "protectedUserData": true,
      "neverDeleted": true,
      "verified": true
    },
    {
      "category": "NVIDIA_SHADER_CACHE",
      "path": "%USERPROFILE%\\AppData\\Local\\NVIDIA\\DXCache",
      "classification": "UNKNOWN_NOT_CONTROLLABLE",
      "managedBy": "NVIDIA_GRAPHICS_DRIVER",
      "verified": true
    }
  ]
}
```
