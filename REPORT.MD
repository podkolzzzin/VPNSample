# VPN end-to-end test report

Test date: 2026-09-04 UTC

Repository: `podkolzzzin/VPNSample`
Tested latest revision: `37dd149d4b5edb32d15eb502c87d8740c6872533` (`origin/main`, `stage-08-peer-to-peer-mesh`)

## Result

All eight tagged versions of the custom protocol successfully carried RUVDS traffic through the DigitalOcean exit server to `https://www.instagram.com/`. Every final custom-protocol run returned HTTP 200 and passed the IPv4/IPv6 tunnel checks.

The comparison tests found different behavior for the standard protocols from RUVDS. WireGuard completed a handshake and carried tunnel ping, but its Instagram request timed out. OpenVPN created the tunnel but failed both tunnel ping and the Instagram request. The same OpenVPN and WireGuard profiles passed handshake, tunnel data, and Instagram from the workspace, so the standard-protocol failures are specific to the RUVDS path or host configuration.

## Custom protocol results

| Version | Commit | Feature | RUVDS tunnel | Instagram | Final run time |
|---|---|---|---|---|---:|
| 1 | `b923a1a8bbfe` | Basic tunnel | PASS | HTTP 200 | 86 s |
| 2 | `51ee485b1b62` | Extensible pipeline | PASS | HTTP 200 | 81 s |
| 3 | `bd1c8945af63` | Shared overlay exit node | PASS | HTTP 200 | 72 s |
| 4 | `d04262ac6b5a` | HTTPS transport | PASS | HTTP 200 | 78 s |
| 5 | `d685a97a4201` | Packet shuffle/split | PASS | HTTP 200 | 87 s |
| 6 | `a6464adba465` | WebSocket cover | PASS | HTTP 200 | 86 s |
| 7 | `519d22fccbd0` | Overlay DNS | PASS | HTTP 200 | 123 s |
| 8 | `37dd149d4b5e` | Peer-to-peer mesh | PASS | HTTP 200 | 119 s |

For every version, the runner deployed matching server and client artifacts from that exact commit, then verified:

- the RUVDS systemd client service and `svpn0` interface;
- IPv4 and IPv6 tunnel-peer ping;
- IPv4 and IPv6 default routes through `svpn0`;
- public IPv4 and IPv6 equal to the configured exit server;
- an HTTPS request to Instagram with redirects followed and an accepted 2xx/3xx response.

Historical versions were checked out as detached temporary Git worktrees. The working repository remained on the latest `main` revision.

## OpenVPN and WireGuard comparison

| Location | Protocol | Handshake/tunnel | Tunnel data | Instagram | Verdict |
|---|---|---|---|---|---|
| Workspace | OpenVPN | PASS | PASS | PASS | WORKS |
| Workspace | WireGuard | PASS | PASS | PASS | WORKS |
| RUVDS | OpenVPN | PASS | FAIL | FAIL | BLOCKED/FAILED |
| RUVDS | WireGuard | PASS | PASS | FAIL | BLOCKED/FAILED |

Instagram was resolved before route changes and pinned with curl's `--resolve`, so the RUVDS standard-protocol failures are not DNS-resolution false negatives. The OpenVPN data probe was ping to `10.90.0.1`; the WireGuard data probe was ping to `10.91.0.1`. Both Instagram failures were connection/SSL timeouts.

## Harness changes and artifacts

Reusable scripts are now stored in this repository under `scripts/`:

- `e2e-all-protocol-versions.sh` — discovers stage tags, creates temporary worktrees, deploys each exact version, captures logs, and optionally runs standard protocols;
- `deploy-client-ruvds.sh` — handles assemblies added by later versions, stops the old service before replacement, uses each commit's default profile, and forwards the WebSocket cover token without printing it;
- `e2e-ruvds.sh` — adds the Instagram HTTPS assertion to the RUVDS custom-protocol E2E;
- `e2e-standard-protocols.sh` — adds Instagram checks to the OpenVPN/WireGuard comparison;
- `setup-openvpn-wireguard.sh` and `ssh-connect-ruvds.sh` — provision the comparison protocols and provide the credential-safe RUVDS SSH wrapper.

The consolidated, sanitized results are in `E2E_RESULTS.tsv`. Raw operational
logs, SSH host records, generated certificates, and VPN client profiles are not
committed because they contain infrastructure identifiers or secret key material.

Early failed attempts were retained in the raw logs. They were caused by three harness compatibility issues found during commit-by-commit testing: replacing a running private .NET host, forcing `baseline` against later servers' default profiles, and omitting the WebSocket cover token. Each issue was corrected before the corresponding final result above.

## Local regression checks

- `dotnet test VPNSample.slnx -c Release --no-restore`: PASS, 58 tests total (34 protocol, 14 DNS, 10 mesh).
- `./scripts/check.sh`: PASS for shared module tests, Bash syntax, help paths, and patch whitespace.
- ShellCheck was not installed, so that optional check was skipped.
