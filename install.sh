#!/usr/bin/env bash
# Builds firstmate-telegram from this clone and installs it for the current user on macOS.
#
# Usage:
#   ./install.sh                       Build and install (or upgrade), run setup when there is no
#                                      configuration yet, install the login agent, then run doctor.
#   ./install.sh --uninstall           Remove the login agent and the app; keep configuration and state.
#   ./install.sh --uninstall --purge   Also remove configuration, state and logs.
#   Add --dry-run to print every change instead of making it.
set -euo pipefail

usage() {
  sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
}

die() {
  printf 'install.sh: %s\n' "$*" >&2
  exit 1
}

dry_run=0
uninstall=0
purge=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) dry_run=1 ;;
    --uninstall) uninstall=1 ;;
    --purge) purge=1 ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      usage >&2
      die "unknown option: $arg"
      ;;
  esac
done
[ "$purge" -eq 0 ] || [ "$uninstall" -eq 1 ] || die "--purge goes with --uninstall"

# Runs a command that changes something, or only prints it under --dry-run.
run() {
  if [ "$dry_run" -eq 1 ]; then
    printf 'would run:'
    printf ' %q' "$@"
    printf '\n'
  else
    "$@"
  fi
}

# XDG folders count only when absolute, as in the bridge itself.
xdg_or_default() {  # <value> <default>
  case "${1:-}" in
    /*) printf '%s' "$1" ;;
    *) printf '%s' "$2" ;;
  esac
}

: "${HOME:?HOME is not set}"
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
label=io.github.lbildzinkas.firstmate-telegram
config_dir="$(xdg_or_default "${XDG_CONFIG_HOME:-}" "$HOME/.config")/firstmate-telegram"
state_dir="$(xdg_or_default "${XDG_STATE_HOME:-}" "$HOME/.local/state")/firstmate-telegram"
log_dir="$HOME/Library/Logs/firstmate-telegram"
app_root="$HOME/.local/share/firstmate-telegram"
app_dir="$app_root/app"
program="$app_dir/firstmate-telegram"
bin_link="$HOME/.local/bin/firstmate-telegram"
plist="$HOME/Library/LaunchAgents/$label.plist"

[ "$(uname -s)" = Darwin ] || die "firstmate-telegram v1 runs on macOS only."

if [ "$uninstall" -eq 1 ]; then
  if [ -x "$program" ]; then
    run "$program" service uninstall
  elif [ -e "$plist" ]; then
    run launchctl bootout "gui/$(id -u)/$label" || true
    run rm -f "$plist"
  fi
  run rm -rf "$app_root"
  if [ -L "$bin_link" ]; then
    run rm -f "$bin_link"
  fi
  if [ "$purge" -eq 1 ]; then
    run rm -rf "$config_dir" "$state_dir" "$log_dir"
    echo "firstmate-telegram is removed, with its configuration, state and logs."
  else
    echo "firstmate-telegram is removed. Configuration and state are kept in $config_dir and $state_dir."
  fi
  exit 0
fi

command -v dotnet >/dev/null 2>&1 || die "the .NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0"
dotnet --list-sdks | grep -q '^10\.' || die "the .NET 10 SDK is required (dotnet --list-sdks shows none): https://dotnet.microsoft.com/download/dotnet/10.0"
case "$(uname -m)" in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) die "unsupported processor: $(uname -m)" ;;
esac

# Publish next to the running app, then swap folders, so an upgrade never rewrites files in use.
staging="$app_root/app.new"
run rm -rf "$staging" "$app_root/app.old"
run dotnet publish "$here/src/FirstmateTelegram/FirstmateTelegram.csproj" \
  --configuration Release --runtime "$rid" --self-contained false --nologo --output "$staging"
if [ -d "$app_dir" ]; then
  run mv "$app_dir" "$app_root/app.old"
fi
run mv "$staging" "$app_dir"
run rm -rf "$app_root/app.old"
run mkdir -p "$(dirname "$bin_link")"
run ln -sfn "$program" "$bin_link"

if [ ! -f "$config_dir/config.json" ]; then
  run "$program" setup
fi
run "$program" service install
run "$program" doctor || echo "doctor found problems above; fix them, then run: firstmate-telegram doctor"
