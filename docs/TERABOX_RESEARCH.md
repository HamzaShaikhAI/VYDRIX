# TeraBox — integration research (status: not supported)

Researched 2026-10-10.

## Findings

- **rclone 1.75.2 has no TeraBox backend** (`rclone help backends` lists none). rclone's maintainers require a
  public API for new backends.
- A community **unofficial fork** (`gulp79/rclone-extra`, code from `x1arch/rclone` branch `backend/terabox`)
  adds TeraBox support. Its own documentation says it authenticates with the **browser session cookie** because
  TeraBox does not provide an official API, and it works by reproducing the web client's undocumented requests.
- TeraBox has announced an "open platform", but no public developer documentation describing OAuth authentication,
  file listing, or byte-range downloads could be found or verified.

## Decision

CLOUDPLAY X does **not** integrate TeraBox:

- The available method depends on session cookies and undocumented endpoints. That conflicts with the project rules
  (no bypassing authentication, no undocumented endpoints, no silent dependence on scraping) and is fragile:
  any web-client change breaks it, and accounts could be flagged.
- Streaming needs reliable HTTP range requests and stable file IDs; neither is documented.

The provider interface keeps a `TeraBox` adapter marked **Unsupported** (`src/Vydrix.Providers/Providers.cs`),
so the UI explains the situation instead of offering a broken option.

## What would change this

An official TeraBox API with (1) OAuth or API-key authentication intended for third-party apps, (2) directory
listing with sizes/modification times, and (3) downloads that honour HTTP `Range`. With those, a rclone backend
(preferred, upstream) or a CLOUDPLAY X provider adapter becomes feasible.

## Alternatives for large cheap storage today

Google Drive (supported), or rclone-backed providers marked "via rclone (untested)": OneDrive, Dropbox,
S3-compatible storage (e.g. Backblaze B2, Wasabi, Cloudflare R2), or a NAS over SMB/SFTP.
