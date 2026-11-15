#!/usr/bin/env bash
set -euo pipefail

profile=reference
if [[ ${1:-} == --minimal ]]; then
    profile=minimal
    shift
fi
if [[ $# -lt 1 || $# -gt 2 || ! $1 =~ ^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*$ ]]; then
    printf 'Usage: %s [--minimal] Project.Name [output-directory]\n' "$0" >&2
    exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
name="$1"
# Compose, PostgreSQL, and C# identifiers all derive from the supplied name.
# Check the generated namespace before writing any output.
reserved=' abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while '
IFS=. read -r -a segments <<< "$name"
if (( ${#name} > 48 )); then
    echo "Project names may contain at most 48 characters." >&2
    exit 2
fi
for segment in "${segments[@]}"; do
    if [[ $reserved == *" $segment "* ]]; then
        printf 'Reserved C# namespace segment: %s\n' "$segment" >&2
        exit 2
    fi
done
output="${2:-$root/../$name}"
if [[ -e "$output" || -L "$output" ]]; then
    printf 'Choose a new output directory: %s\n' "$output" >&2
    exit 2
fi

dotnet new install "$root" --force >/dev/null
dotnet new super-backend --name "$name" --output "$output"
# Template engines may normalize the permissions on copied source files.
chmod +x "$output"/scripts/*.sh "$output"/deploy/reference/init-roles.sh
if [[ $profile == minimal ]]; then
    python3 - "$output/src/$name.Api/appsettings.Development.json" <<'PYPROFILE'
import json
from pathlib import Path
import sys
settings = Path(sys.argv[1])
data = json.loads(settings.read_text())
data["Modules"] = {name: False for name in data["Modules"]}
settings.write_text(json.dumps(data, indent=4) + "\n")
PYPROFILE
fi
