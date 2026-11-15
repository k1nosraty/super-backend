#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 || ! $1 =~ ^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*$ ]]; then
    printf 'Usage: %s Project.Name [output-directory]\n' "$0" >&2
    exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
name="$1"
output="${2:-$root/../$name}"
if [[ -e "$output" || -L "$output" ]]; then
    printf 'Choose a new output directory: %s\n' "$output" >&2
    exit 2
fi

dotnet new install "$root" --force >/dev/null
dotnet new super-backend --name "$name" --output "$output"
# Template engines may normalize the permissions on copied source files.
chmod +x "$output"/scripts/*.sh
