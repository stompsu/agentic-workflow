#!/usr/bin/env bash
# Placeholder-free (safe for CI): fails if main.bicep applies string() to a bool.
set -euo pipefail
dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bad=0
# Bool -> string guard: Bicep string(bool) renders "True"/"False", but the frontend entrypoint and ASP.NET config want
# lowercase. Env values must use `cond ? 'true' : 'false'`. Flags string() over bool params and boolean-looking vars.
main="$dir/main.bicep"
boolnames="$(sed -nE 's/^[[:space:]]*param[[:space:]]+([A-Za-z0-9_]+)[[:space:]]+bool\b.*/\1/p' "$main")"
boolnames="$boolnames $(grep -E "^var[[:space:]]+[A-Za-z0-9_]+[[:space:]]*=.*(==|!=|&&|\|\||\bbool\()" "$main" | sed -E 's/^var[[:space:]]+([A-Za-z0-9_]+).*/\1/')"
for n in $boolnames; do
  if grep -nE "string\([[:space:]]*${n}[[:space:]]*\)" "$main" | grep -vE '^[0-9]+:[[:space:]]*//'; then
    echo "ERROR: string($n) on a bool in main.bicep renders True/False; use $n ? 'true' : 'false'" >&2; bad=1
  fi
done
[ $bad -eq 0 ] && echo "check-bool-env: OK"
exit $bad
