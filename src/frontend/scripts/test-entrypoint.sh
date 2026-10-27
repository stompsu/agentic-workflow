#!/bin/sh
# Script test for docker-entrypoint.d/10-write-config.sh (needs jq). Run: npm run test:entrypoint
# Checks: no placeholder remains, & < > " ' in env values are escaped in /data-deletion.html, values reach
# config.json as JSON, input validation fails fast.
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
FAILED=0

ok() { echo "ok   - $1"; }
bad() { echo "FAIL - $1" >&2; FAILED=1; }
# assert <description> <command...>: the command must succeed. refute: it must fail.
assert() { desc="$1"; shift; if "$@" >/dev/null 2>&1; then ok "$desc"; else bad "$desc"; fi; }
refute() { desc="$1"; shift; if "$@" >/dev/null 2>&1; then bad "$desc"; else ok "$desc"; fi; }

# run_entrypoint <dir> [VAR=value ...]; stdout/stderr go to <dir>/out.
run_entrypoint() {
  dir="$1"; shift
  rm -rf "$dir"
  mkdir -p "$dir/html" "$dir/conf"
  cp "$ROOT/public/data-deletion.html" "$dir/html/data-deletion.html"
  printf 'nameserver 127.0.0.1\n' > "$dir/resolv.conf"
  env -i PATH="$PATH" HTML_DIR="$dir/html" CONF_DIR="$dir/conf" RESOLV_CONF="$dir/resolv.conf" \
    BACKEND_UPSTREAM=backend.internal.example.io AUTH_CLIENT_ID=cid AUTH_AUTHORITY=https://t.ciamlogin.com/ \
    AUTH_API_SCOPE=api://x/access_as_user "$@" sh "$ROOT/docker-entrypoint.d/10-write-config.sh" > "$dir/out" 2>&1
}

# 1. defaults: nothing optional set
d="$WORK/default"
assert "default run succeeds" run_entrypoint "$d"
page="$d/html/data-deletion.html"
refute "no placeholder left (defaults)" grep -q '__[A-Z_]*__' "$page"
assert "default title" grep -qF 'Delete your data - Assistant' "$page"
assert "default retention text" grep -qF 'deleted automatically after a retention period.' "$page"
assert "default contact text" grep -qF 'contact the operator of this service.' "$page"
assert "config.json omits unset fields" jq -e '.app | (has("supportContact") | not) and (has("retentionDays") | not)' "$d/html/config.json"

# 2. hostile values: escaped in HTML, verbatim in JSON, no sed/awk metacharacter trouble
d="$WORK/hostile"
# shellcheck disable=SC2016  # the dollar sign and backticks are deliberate literal test data
TITLE='A&B <script>alert(1)</script> "q" '"'"'s'"'"' \1 & /x/ $(id) `id`'
CONTACT='x@y.z <b>&amp;</b> "c" '"'"'d'"'"' \0 & /e/ __APP_TITLE__'
assert "hostile run succeeds" run_entrypoint "$d" APP_TITLE="$TITLE" APP_RETENTION_DAYS=90 APP_SUPPORT_CONTACT="$CONTACT"
page="$d/html/data-deletion.html"
refute "no placeholder left (hostile)" grep -q '__RETENTION_TEXT__\|__SUPPORT_CONTACT_TEXT__' "$page"
refute "no raw <script in page" grep -qF '<script' "$page"
refute "no raw <b> in page" grep -qF '<b>' "$page"
# shellcheck disable=SC2016
assert "title escaped exactly" grep -qF 'A&amp;B &lt;script&gt;alert(1)&lt;/script&gt; &quot;q&quot; &#39;s&#39; \1 &amp; /x/ $(id) `id`' "$page"
assert "contact escaped exactly, no re-substitution" grep -qF 'contact x@y.z &lt;b&gt;&amp;amp;&lt;/b&gt; &quot;c&quot; &#39;d&#39; \0 &amp; /e/ __APP_TITLE__.' "$page"
assert "retention text" grep -qF 'each message 90 days after it was sent, and each conversation 90 days after its last activity.' "$page"
# shellcheck disable=SC2016  # $c and $t are jq variables
assert "config.json values exact" jq -e --arg c "$CONTACT" --arg t "$TITLE" '.app.supportContact == $c and .app.title == $t and .app.retentionDays == 90' "$d/html/config.json"
leftovers="$(find "$d/html" -name '.*' -type f)"
assert "no temp files left" test -z "$leftovers"

# 3. validation
for v in 0 3651 abc 01 -5 '1 2' 99999; do
  refute "APP_RETENTION_DAYS=$v rejected" run_entrypoint "$WORK/v" APP_RETENTION_DAYS="$v"
done
for v in 1 3650; do
  assert "APP_RETENTION_DAYS=$v accepted" run_entrypoint "$WORK/v" APP_RETENTION_DAYS="$v"
done
LONG="$(awk 'BEGIN { for (i = 0; i < 201; i++) printf "a" }')"
refute "201-char contact rejected" run_entrypoint "$WORK/long" APP_SUPPORT_CONTACT="$LONG"
OK200="$(awk 'BEGIN { for (i = 0; i < 200; i++) printf "a" }')"
assert "200-char contact accepted" run_entrypoint "$WORK/ok200" APP_SUPPORT_CONTACT="$OK200"
NL="$(printf 'a\nb')"
refute "contact with newline rejected" run_entrypoint "$WORK/nl" APP_SUPPORT_CONTACT="$NL"

# 4. guest keys
d="$WORK/guest-default"
run_entrypoint "$d"
assert "guest disabled by default, default label" jq -e '.auth.guest == {"enabled": false, "label": "Continue as guest"} and (.app | has("guestSessionHours") | not) and (.app | has("guestRetentionHours") | not)' "$d/html/config.json"
d="$WORK/guest-on"
assert "guest enabled run succeeds" run_entrypoint "$d" AUTH_GUEST_ENABLED=true AUTH_GUEST_LABEL='Try it <now> & "go"' APP_GUEST_SESSION_HOURS=4 APP_GUEST_RETENTION_HOURS=5
assert "guest keys reach config.json (label verbatim, hours numeric)" jq -e '.auth.guest == {"enabled": true, "label": "Try it <now> & \"go\""} and .app.guestSessionHours == 4 and .app.guestRetentionHours == 5' "$d/html/config.json"
for v in yes TRUE 1 ' true'; do
  refute "AUTH_GUEST_ENABLED='$v' rejected" run_entrypoint "$WORK/g" AUTH_GUEST_ENABLED="$v"
done
for v in 0 13 abc 01 -1 100; do
  refute "APP_GUEST_SESSION_HOURS=$v rejected" run_entrypoint "$WORK/g" APP_GUEST_SESSION_HOURS="$v"
done
for v in 1 12; do
  assert "APP_GUEST_SESSION_HOURS=$v accepted" run_entrypoint "$WORK/g" APP_GUEST_SESSION_HOURS="$v"
done
for v in 0 169 1000 abc 007; do
  refute "APP_GUEST_RETENTION_HOURS=$v rejected" run_entrypoint "$WORK/g" APP_GUEST_RETENTION_HOURS="$v"
done
for v in 1 168; do
  assert "APP_GUEST_RETENTION_HOURS=$v accepted" run_entrypoint "$WORK/g" APP_GUEST_RETENTION_HOURS="$v"
done
LONG61="$(awk 'BEGIN { for (i = 0; i < 61; i++) printf "a" }')"
refute "61-char guest label rejected" run_entrypoint "$WORK/g" AUTH_GUEST_LABEL="$LONG61"
refute "guest label with newline rejected" run_entrypoint "$WORK/g" AUTH_GUEST_LABEL="$NL"

if [ "$FAILED" -eq 0 ]; then echo "entrypoint tests passed"; else echo "entrypoint tests FAILED" >&2; exit 1; fi
