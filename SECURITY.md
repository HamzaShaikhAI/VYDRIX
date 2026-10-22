# Security

CLOUDPLAY X handles Google sign-ins, so please report security problems privately: use GitHub's
**Security → Report a vulnerability** on this repository instead of opening a public issue.

How credentials are handled:

- Google sign-in happens in your browser (OAuth via rclone). CLOUDPLAY X never sees your Google password.
- The saved rclone login is encrypted with a key protected by Windows DPAPI (current user).
- rclone's remote-control API listens on 127.0.0.1 only, with random credentials per session.
- Logs and diagnostic reports pass through a redactor that removes tokens, client secrets and passwords.

Never put OAuth client secrets, tokens or `rclone.conf` in issues, pull requests or commits. The optional publisher
key `installer/google-client.json` is ignored by git on purpose.
