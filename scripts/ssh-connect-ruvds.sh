#!/usr/bin/env bash
set -Eeuo pipefail

: "${RUVDS_IP:?RUVDS_IP is required}"
: "${RUVDS_USER:?RUVDS_USER is required}"
: "${RUVDS_PASSWORD:?RUVDS_PASSWORD is required}"

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
known_hosts=$script_dir/.ruvds_known_hosts
askpass=$(mktemp /tmp/ruvds-askpass.XXXXXXXX)
cleanup() { rm -f -- "$askpass"; }
trap cleanup EXIT
printf '#!/usr/bin/env bash\nprintf '\''%%s\\n'\'' "$RUVDS_PASSWORD"\n' >"$askpass"
chmod 700 "$askpass"

export SSH_ASKPASS=$askpass SSH_ASKPASS_REQUIRE=force
setsid -w ssh \
  -o StrictHostKeyChecking=accept-new \
  -o "UserKnownHostsFile=$known_hosts" \
  -o PreferredAuthentications=password \
  -o PubkeyAuthentication=no \
  "$RUVDS_USER@$RUVDS_IP" "$@"
