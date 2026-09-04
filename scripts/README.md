# Automation scripts

The executable files in this directory are intentionally thin workflows:

| Entrypoint | Responsibility |
|---|---|
| `create-droplet.sh` | Create or delete one state-file-owned DigitalOcean droplet |
| `deploy-server.sh` | Publish and deploy the VPN server to that droplet |
| `run-vpn.sh` | Run the local client and temporarily manage routes plus private DNS |
| `e2e-three-node.sh` | Demonstrate three-region routing, DNS names, and nginx access |
| `checkout_next_tag.sh` | Move to the next tagged demo stage |
| `checkout_prev_tag.sh` | Move to the previous tagged demo stage |

Supporting code is grouped by where it runs:

- `lib/` contains reusable functions executed on the operator's machine.
- `remote/` contains complete scripts streamed or copied to a Linux VM.
- The repository-root `e2e_vpn_iptest.sh` remains a compatibility entrypoint;
  its remote test implementation lives in `remote/test-exit-ip.sh`.

Entrypoints own argument parsing and the high-level sequence. Shared modules do
not parse CLI arguments. Remote scripts receive every deployment-specific value
as an explicit positional argument, which keeps shell expansion on the correct
side of the SSH boundary.

## Checks

Run the local, non-destructive shell checks with:

```bash
./scripts/check.sh
```

This always runs the shared-module smoke tests and checks Bash syntax, CLI help
paths, and patch whitespace. It also runs ShellCheck when that executable is
installed.

The full disposable integration test is:

```bash
./scripts/e2e-three-node.sh
```

The current stage defaults deployment and client scripts to the
`websocket-cover` profile. Deployment generates `VPN_COVER_TOKEN`, stores it in
the selected state file, and supplies it to clients without logging it. Set
`VPN_PROFILE=shuffle-split` or `VPN_PROFILE=baseline` consistently on both sides
to compare pipeline behavior while retaining the WebSocket transport.

`run-vpn.sh --name NAME` registers `NAME.vpn` and routes the private zone to the
server through `systemd-resolved`. The three-node E2E registers `nginx-node` and
`requester-node`, verifies their A and AAAA answers, then requests nginx by DNS
name rather than by an address.
