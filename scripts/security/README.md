# The security test scripts

They are run by hand in week 8 as part of the acceptance list, and some of them automatically in CI against staging.

| Script | Checks | Document requirement |
|---|---|---|
| `check-session-keys.sh` | No keys left over for ended sessions | 6.7, 14 |
| `check-listener.sh <host> <port>` | The host's listener: TLS only, silent before authentication, closes strangers, a temporary self-signed certificate | 14, protocol.md §2 |
| `check-egress-blocks.sh <proxy-port>` | Refusal of localhost, the local network and internal addresses through the proxy, and passage of what is allowed | 6.6, 14, protocol.md §6 |
| `check-logs-clean.sh [path...]` | No URLs, headers or secrets in the logs | 6.8, 14, 15 |

Every script returns 0 on success and something else on failure, so it is suitable for running in CI.

## Examples

```bash
scripts/security/check-session-keys.sh
scripts/security/check-listener.sh 203.0.113.10 51234
scripts/security/check-egress-blocks.sh 49152
scripts/security/check-logs-clean.sh ~/Library/Logs/Josour/app.log
```

On Windows they are run from Git Bash or WSL; `check-listener.sh` needs `openssl` and `nc`, and makes use of `nmap` if
it is present.
