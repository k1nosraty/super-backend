#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 || ! $1 =~ ^[A-Z][A-Za-z0-9]{0,63}$ ]]; then
    printf 'Usage: %s ServiceName (PascalCase, up to 64 characters)\n' "$0" >&2
    exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
shopt -s nullglob nocasematch
api_projects=(src/*.Api/*.csproj)
solutions=(*.slnx)
[[ ${#api_projects[@]} == 1 && ${#solutions[@]} == 1 ]] || { echo "Expected one API and solution." >&2; exit 1; }

api="${api_projects[0]}"
solution="${solutions[0]}"
project="$(basename "${api%.Api.csproj}")"
service="$1"
module="src/Modules/$project.$service"
registration="$(dirname "$api")/ProductModules.cs"
lock="$root/.service-scaffold.lock"
if ! mkdir "$lock"; then
    echo "Another scaffold may be editing this solution. Check $lock." >&2
    exit 1
fi
staging=""
cleanup() {
    if [[ -n "$staging" ]]; then rm -rf -- "$staging"; fi
    rmdir -- "$lock"
}
trap cleanup EXIT

for directory in src/Modules/*; do
    if [[ "$directory" == "$module" ]]; then
        printf 'Service already exists: %s\n' "$directory" >&2
        exit 2
    fi
done

staging="$(mktemp -d)"
cp "$api" "$staging/api.csproj"
cp "$solution" "$staging/solution.slnx"
cp "$registration" "$staging/registration.cs"

python3 - "$project" "$service" "$staging/module" "$staging/registration.next.cs" "$registration" <<'PY'
from pathlib import Path
import re
import sys

project, service, target, next_registration, registration = sys.argv[1:]
route = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", "-", service).lower()
values = {"__ROOT__": project, "__SERVICE__": service, "__ROUTE__": route}

def render(text: str) -> str:
    for key, value in values.items():
        text = text.replace(key, value)
    return text

template = Path("templates/service")
marker = "        // Service registration"
current = Path(registration).read_text()
if current.count(marker) != 1:
    raise SystemExit("The service registration marker could not be located.")
statement = f"        global::{project}.{service}.{service}Module.Register(builder, mvc);"
if statement in current:
    raise SystemExit("The service is already registered.")
for source in template.rglob("*.tmpl"):
    destination = Path(target) / render(str(source.relative_to(template)).removesuffix(".tmpl"))
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(render(source.read_text()))
Path(next_registration).write_text(current.replace(marker, "\n".join((statement, marker))))
PY

mv -- "$staging/module" "$module"
if ! dotnet sln "$solution" add "$module" --solution-folder src/Modules ||
   ! dotnet add "$api" reference "$module" ||
   ! cp "$staging/registration.next.cs" "$registration"; then
    cp "$staging/api.csproj" "$api"
    cp "$staging/solution.slnx" "$solution"
    cp "$staging/registration.cs" "$registration"
    rm -rf -- "$module"
    exit 1
fi

if ! dotnet restore; then
    printf 'Service files are registered. Run dotnet restore to finish.\n' >&2
    exit 1
fi
printf 'Created %s. Enable with Modules__%s=true.\n' "$module" "$service"
