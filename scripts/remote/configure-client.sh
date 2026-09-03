#!/usr/bin/env bash
set -Eeuo pipefail

install_root=$1
install_nginx=${2:-false}
install_probe_tools=${3:-false}
export DEBIAN_FRONTEND=noninteractive

if command -v cloud-init >/dev/null 2>&1; then
  cloud-init status --wait >/dev/null
fi

packages=(ca-certificates curl iproute2 iputils-ping)
[[ $install_nginx == true ]] && packages+=(nginx)
[[ $install_probe_tools == true ]] && packages+=(net-tools python3)
apt-get -o DPkg::Lock::Timeout=300 update -qq
apt-get -o DPkg::Lock::Timeout=300 install -y -qq "${packages[@]}" >/dev/null

mkdir -p "$install_root/app" "$install_root/dotnet"
if ! "$install_root/dotnet/dotnet" --list-runtimes 2>/dev/null \
    | grep -q '^Microsoft.NETCore.App 10\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/vpnsample-dotnet-install.sh
  bash /tmp/vpnsample-dotnet-install.sh --channel 10.0 --runtime dotnet \
    --install-dir "$install_root/dotnet" >/dev/null
  rm -f /tmp/vpnsample-dotnet-install.sh
fi

if [[ $install_nginx == true ]]; then
  printf '%s\n' 'vpn-mesh-nginx-ok' >/var/www/html/index.html
  systemctl enable --now nginx >/dev/null
fi
