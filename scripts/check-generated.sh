#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$(mktemp -d)"
database=""
port="${GENERATED_CHECK_PORT:-15984}"
compose() { docker compose --project-directory "$root" -f "$root/compose.yaml" "$@"; }
cleanup() {
    if [[ -n $database ]]; then compose exec -T database dropdb -U superbackend --if-exists --force "$database"; fi
    rm -rf -- "$scratch"
}
trap cleanup EXIT
cd "$root"

for invalid in class Example.class Example._Invalid; do
    if ./scripts/new-project.sh "$invalid" "$scratch/refused"; then
        echo "Invalid name accepted: $invalid" >&2
        exit 1
    fi
    test ! -e "$scratch/refused"
done

verify() {
    local name="$1" profile="$2" db_suffix="$3" options=()
    [[ $profile != minimal ]] || options=(--minimal)
    ./scripts/new-project.sh "${options[@]}" "$name" "$scratch/$name"
    if ./scripts/new-project.sh "$name" "$scratch/$name"; then
        echo "An existing generated solution was accepted as an output path." >&2
        exit 1
    fi
    database="template_check_${db_suffix}_$$"
    compose exec -T database createdb -U superbackend "$database"
    (
        cd "$scratch/$name"
        test ! -e scripts/new-project.sh
        if [[ $profile == minimal ]]; then
            ./scripts/new-service.sh DispatchReports --minimal
            export Modules__DispatchReports=true
            route=dispatch-reports
            test ! -d "src/Modules/$name.DispatchReports/Services"
        else
            ./scripts/new-service.sh InventoryReports
            export Modules__InventoryReports=true
            route=inventory-reports
            test -f "src/Modules/$name.InventoryReports/Services/InventoryReportsService.cs"
        fi
        dotnet restore --locked-mode
        dotnet build -c Release --no-restore
        export ConnectionStrings__Main="Host=127.0.0.1;Port=${SUPERBACKEND_DB_PORT:-54329};Database=$database;Username=superbackend;Password=local-dev-only;GSS Encryption Mode=Disable"
        export ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:$port"
        app="$scratch/$name/src/$name.Api/bin/Release/net10.0"
        cd "$app"
        dotnet "$name.Api.dll" --check-profile
        dotnet "$name.Api.dll" --migrate
        dotnet "$name.Api.dll" >"$scratch/$db_suffix.log" 2>&1 &
        child=$!
        trap 'kill "$child" 2>/dev/null || true; wait "$child" 2>/dev/null || true' EXIT
        if ! curl --silent --show-error --fail --retry 10 --retry-delay 1 --retry-all-errors \
            "http://127.0.0.1:$port/health/ready"; then
            cat "$scratch/$db_suffix.log"
            exit 1
        fi
        # The fallback bearer challenge can hide a missing route.
        # Inspect development OpenAPI for route presence.
        curl --silent --show-error --fail "http://127.0.0.1:$port/openapi/v1.json" |
            python3 -c 'import json,sys; paths=json.load(sys.stdin)["paths"]; assert ("/api/v1/notes" in paths) == (sys.argv[1]=="reference"); assert "/api/v1/features/" + sys.argv[2] in paths' "$profile" "$route"
    )
    compose exec -T database dropdb -U superbackend --force "$database"
    database=""
    printf '\nVerified %s (%s).\n' "$name" "$profile"
}
verify Example.Inventory reference inventory
verify Northwind.Dispatch minimal dispatch
