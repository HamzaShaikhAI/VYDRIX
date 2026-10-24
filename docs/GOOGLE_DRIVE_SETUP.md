# Connecting Google Drive

VYDRIX connects to Google Drive through rclone using **your own OAuth client**. rclone's built-in shared client
ID is being retired during 2026 (rclone 1.75.2 says so in `rclone help backend drive`), and a personal client also
avoids the shared client's rate limits.

You never type your Google password into VYDRIX. Sign-in happens on Google's own page in your browser; rclone
receives the resulting token on `http://127.0.0.1:53682/` and stores it in VYDRIX's private rclone config.

## Quick start: one-click sign-in

**Cloud Drives → Sign in with Google Drive → Sign in with Google.** Your browser opens Google's page; pick your
account and click *Allow* (the consent page says "rclone"). Your whole Google Drive is then mounted automatically as a
drive letter.

This uses rclone's built-in shared Google client. It works today (verified 2026-10-11), but rclone is retiring it
during 2026, and it is shared by all rclone users, so it can be throttled or stop working. If it fails, use your own
client as described below (tick **Advanced** in the same window).

## 1. Create an OAuth client (one time, ~5 minutes)

1. Open <https://console.cloud.google.com/> and create a project (any name, e.g. "VYDRIX").
2. **APIs & Services → Library** → search "Google Drive API" → **Enable**.
3. **APIs & Services → OAuth consent screen** → User type **External** → fill in the app name and your email →
   on **Test users** add your own Google account. (Leaving the app in "Testing" is fine for personal use; note that
   Google expires refresh tokens of apps in Testing mode after 7 days — publish the app to "In production"
   (no verification needed for personal use of the `drive` scope with a warning screen) to avoid weekly re-logins.)
4. **APIs & Services → Credentials → Create credentials → OAuth client ID** → Application type **Desktop app**.
5. Copy the **Client ID** (ends with `.apps.googleusercontent.com`) and the **Client secret**.

## 2. Connect in VYDRIX

1. **Cloud Drives → Connect Google Drive**.
2. Enter an account name (e.g. `gdrive`), the client ID and the client secret.
3. Optional: **Read-only access** (`drive.readonly`) — safest for a game library; save games must then stay local
   (use Hybrid mode or games that save to Documents/AppData, which most do).
4. Optional: a **root folder ID** to limit VYDRIX to one Drive folder (the ID is the last part of the folder's URL).
5. Click **Connect**. Your browser opens Google's consent page; approve it. VYDRIX verifies access with a
   quota query and then encrypts the saved login (see below).
6. **Add virtual drive**: pick the account, browse to your games folder (only folder names are listed), choose a
   drive letter and mount.

## Where the login is stored

- `%LOCALAPPDATA%\Vydrix\rclone.conf` — VYDRIX's own rclone config (your normal rclone config is not used).
- With **Settings → Encrypt the rclone config** (default on) the file is encrypted by rclone itself. The random
  encryption key is stored in `rclone.conf.key`, protected with Windows DPAPI for your user account, and handed to
  rclone only through an environment variable — never on a command line.
- The client secret is sent only to a local rclone daemon on 127.0.0.1 in an HTTP request body.
- Diagnostic exports and logs redact tokens, client secrets and passwords; the rclone config is never exported.

## Quotas and errors

- Google Drive API quotas apply. VYDRIX limits rclone to 10 API transactions/s by default
  (Settings → Network) and rclone retries rate-limit responses with exponential backoff.
- "Cloud authentication required": the token expired or was revoked (or the app is in Testing mode and 7 days have
  passed). Reconnect the account.
- Very large single files are fine: reads use HTTP byte ranges, so only the parts the game reads are downloaded.
