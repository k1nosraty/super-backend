#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
shopt -s nullglob
api_projects=("$root"/src/*.Api/*.csproj)
[[ ${#api_projects[@]} == 1 ]] || { echo "Expected one API project." >&2; exit 1; }
api="${api_projects[0]}"
project="$(basename "${api%.Api.csproj}")"

usage() {
    cat >&2 <<USAGE
Usage:
  $0 add Module MigrationName [DbContext]
  $0 sql Module Output.sql [DbContext]
  $0 check Module [DbContext]
  $0 apply
  $0 backup Output.dump
  $0 restore Input.dump NewDatabase
USAGE
    exit 2
}

compose() { docker compose --project-directory "$root" -f "$root/compose.yaml" "$@"; }

ef() {
    local module="$1" context="$2"
    shift 2
    [[ $module =~ ^[A-Za-z][A-Za-z0-9]*$ ]] || usage
    local target="$root/src/Modules/$project.$module/$project.$module.csproj"
    [[ -f "$target" ]] || { printf 'Module project not found: %s\n' "$target" >&2; exit 2; }
    (
        cd "$root"
        dotnet tool restore
        if [[ $module == Outbox ]]; then export Modules__Jobs=true; fi
        local build_options=()
        if [[ ${DB_CHECK_CONFIGURATION:-} == Release && $1 == has-pending-model-changes ]]; then
            build_options=(--no-build --configuration Release)
        fi
        env ASPNETCORE_ENVIRONMENT=Development "Modules__${module}=true" \
            dotnet ef migrations "$@" "${build_options[@]}" --context "$context" --project "$target" --startup-project "$api"
    )
}

case "${1:-}" in
    add)
        [[ $# -ge 3 && $# -le 4 && $3 =~ ^[A-Za-z][A-Za-z0-9_]*$ ]] || usage
        ef "$2" "${4:-${2}Data}" add "$3" --output-dir Migrations
        ;;
    sql)
        [[ $# -ge 3 && $# -le 4 ]] || usage
        destination="$3"
        [[ $destination == /* ]] || destination="$PWD/$destination"
        ef "$2" "${4:-${2}Data}" script --idempotent --output "$destination"
        ;;
    check)
        [[ $# -ge 2 && $# -le 3 ]] || usage
        ef "$2" "${3:-${2}Data}" has-pending-model-changes
        ;;
    apply)
        [[ $# == 1 ]] || usage
        (
            cd "$root"
            ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}" \
                dotnet run --project "$api" --no-launch-profile -- --migrate
        )
        ;;
    backup)
        [[ $# == 2 ]] || usage
        umask 077
        temporary="$(mktemp -- "${2}.XXXXXX")"
        trap 'rm -f -- "$temporary"' EXIT
        compose exec -T database pg_dump --username=superbackend --dbname=superbackend --format=custom > "$temporary"
        ln -- "$temporary" "$2"
        printf 'Backup written to %s\n' "$2"
        ;;
    restore)
        [[ $# == 3 && -r $2 && ${#3} -le 63 && $3 =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || usage
        compose exec -T database pg_restore --list < "$2" >/dev/null
        compose exec -T database createdb --username=superbackend -- "$3"
        if ! compose exec -T database pg_restore --username=superbackend --dbname="$3" \
                --exit-on-error --single-transaction --no-owner --no-privileges < "$2"; then
            compose exec -T database dropdb --username=superbackend -- "$3"
            exit 1
        fi
        printf 'Backup restored to %s\n' "$3"
        ;;
    *) usage ;;
esac
