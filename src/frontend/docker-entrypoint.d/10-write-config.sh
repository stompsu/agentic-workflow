#!/bin/sh
# Runs before the nginx image's own entrypoint scripts (20-envsubst-on-templates.sh).
#  1. validates BACKEND_UPSTREAM (it is substituted into nginx config)
#  2. writes the resolver include (DNS servers from resolv.conf)
#  3. writes /config.json from environment variables, JSON-escaped via jq (no secrets are involved)
#  4. renders /data-deletion.html: replaces __APP_TITLE__, __RETENTION_TEXT__, __SUPPORT_CONTACT_TEXT__ in place with
#     HTML-escaped values (jq only: values never pass through sed/awk/shell substitution), temp file + rename
#
# Env vars (all non-secret):
#   BACKEND_UPSTREAM        required  backend internal FQDN, host only (e.g. ca-x-backend.internal.<env>.<region>.azurecontainerapps.io)
#   AUTH_CLIENT_ID          required  SPA app registration client id
#   AUTH_AUTHORITY          required  https://<sub>.ciamlogin.com/
#   AUTH_API_SCOPE          required  api://<api-id>/access_as_user (comma separated for several scopes)
#   AUTH_PROVIDERS          optional  JSON array [{"id","label","domainHint"}]; default Google + Facebook
#   AUTH_KNOWN_AUTHORITIES  optional  comma separated hosts; default = host of AUTH_AUTHORITY
#   AUTH_REDIRECT_URI       optional  default /redirect.html (MSAL v5 redirect bridge; register <origin>/redirect.html)
#   APP_TITLE               optional  application name (login page, top bar, document title); default Assistant
#   APP_TAGLINE             optional  one line under the title on the login page; default "Sign in to continue."
#   APP_RETENTION_DAYS      optional  integer 1..3650 (same value as backend Chat:RetentionDays); config.json app.retentionDays
#                                     and /data-deletion.html. Invalid value = startup failure.
#   APP_SUPPORT_CONTACT     optional  plain text, max 200 chars, no control characters (e.g. an email address);
#                                     config.json app.supportContact and /data-deletion.html. Always rendered as text.
#   AUTH_GUEST_ENABLED      optional  true | false (default false): shows "Continue as guest" (config.json auth.guest.enabled).
#                                     Must match the backend Guest__Enabled. Anything else = startup failure.
#   AUTH_GUEST_LABEL        optional  button label, plain text, max 60 chars, no control characters; default "Continue as guest"
#   APP_GUEST_SESSION_HOURS optional  integer 1..12 (= backend Guest:MaxLifetime in hours); config.json app.guestSessionHours
#   APP_GUEST_RETENTION_HOURS optional integer 1..168 (= Guest:DataTtl rounded up to hours); config.json app.guestRetentionHours
#   API_BASE_URL            optional  default /api
set -eu

HTML_DIR="${HTML_DIR:-/usr/share/nginx/html}"
CONF_DIR="${CONF_DIR:-/etc/nginx/conf.d}"
RESOLV_CONF="${RESOLV_CONF:-/etc/resolv.conf}"

fail() { echo "10-write-config: $*" >&2; exit 1; }

: "${BACKEND_UPSTREAM:?BACKEND_UPSTREAM is required}"
: "${AUTH_CLIENT_ID:?AUTH_CLIENT_ID is required}"
: "${AUTH_AUTHORITY:?AUTH_AUTHORITY is required}"
: "${AUTH_API_SCOPE:?AUTH_API_SCOPE is required}"

# Host name only: no scheme, port, path or characters that could alter the nginx config.
printf '%s' "$BACKEND_UPSTREAM" | grep -Eq '^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$' \
  || fail "BACKEND_UPSTREAM must be a bare host name (no scheme or port)"

DEFAULT_PROVIDERS='[{"id":"google","label":"Continue with Google","domainHint":"google"},{"id":"facebook","label":"Continue with Facebook","domainHint":"facebook"}]'
AUTH_PROVIDERS="${AUTH_PROVIDERS:-$DEFAULT_PROVIDERS}"
AUTH_REDIRECT_URI="${AUTH_REDIRECT_URI:-/redirect.html}"
API_BASE_URL="${API_BASE_URL:-/api}"
APP_TITLE="${APP_TITLE:-Assistant}"
APP_TAGLINE="${APP_TAGLINE:-Sign in to continue.}"

APP_RETENTION_DAYS="${APP_RETENTION_DAYS:-}"
APP_SUPPORT_CONTACT="${APP_SUPPORT_CONTACT:-}"

AUTH_GUEST_ENABLED="${AUTH_GUEST_ENABLED:-false}"
AUTH_GUEST_LABEL="${AUTH_GUEST_LABEL:-Continue as guest}"
APP_GUEST_SESSION_HOURS="${APP_GUEST_SESSION_HOURS:-}"
APP_GUEST_RETENTION_HOURS="${APP_GUEST_RETENTION_HOURS:-}"

case "$AUTH_GUEST_ENABLED" in
  true|false) ;;
  *) fail "AUTH_GUEST_ENABLED must be true or false" ;;
esac
jq -n -e --arg v "$AUTH_GUEST_LABEL" '($v | length) >= 1 and ($v | length) <= 60 and ($v | explode | any(. < 32 or . == 127) | not)' >/dev/null \
  || fail "AUTH_GUEST_LABEL must be 1 to 60 characters of plain text without control characters"
if [ -n "$APP_GUEST_SESSION_HOURS" ]; then
  printf '%s' "$APP_GUEST_SESSION_HOURS" | grep -Eq '^[1-9][0-9]?$' && [ "$APP_GUEST_SESSION_HOURS" -le 12 ] \
    || fail "APP_GUEST_SESSION_HOURS must be an integer from 1 to 12"
fi
if [ -n "$APP_GUEST_RETENTION_HOURS" ]; then
  printf '%s' "$APP_GUEST_RETENTION_HOURS" | grep -Eq '^[1-9][0-9]{0,2}$' && [ "$APP_GUEST_RETENTION_HOURS" -le 168 ] \
    || fail "APP_GUEST_RETENTION_HOURS must be an integer from 1 to 168"
fi

if [ -n "$APP_RETENTION_DAYS" ]; then
  printf '%s' "$APP_RETENTION_DAYS" | grep -Eq '^[1-9][0-9]{0,3}$' && [ "$APP_RETENTION_DAYS" -le 3650 ] \
    || fail "APP_RETENTION_DAYS must be an integer from 1 to 3650"
fi
if [ -n "$APP_SUPPORT_CONTACT" ]; then
  jq -n -e --arg v "$APP_SUPPORT_CONTACT" '($v | length) <= 200 and ($v | explode | any(. < 32 or . == 127) | not)' >/dev/null \
    || fail "APP_SUPPORT_CONTACT must be at most 200 characters of plain text without control characters"
fi

if [ -z "${AUTH_KNOWN_AUTHORITIES:-}" ]; then
  AUTH_KNOWN_AUTHORITIES="$(printf '%s' "$AUTH_AUTHORITY" | sed -E 's#^[A-Za-z][A-Za-z0-9+.-]*://([^/:]+).*#\1#')"
fi

printf '%s' "$AUTH_PROVIDERS" | jq -e 'type == "array" and all(.[]; (.id | type == "string") and (.label | type == "string"))' >/dev/null \
  || fail "AUTH_PROVIDERS must be a JSON array of {id,label,domainHint?}"

# resolver include: every nameserver from resolv.conf (IPv6 addresses bracketed), ipv6=off for lookups.
NS="$(awk '$1 == "nameserver" { if ($2 ~ /:/) printf "[%s] ", $2; else printf "%s ", $2 }' "$RESOLV_CONF")"
[ -n "$NS" ] || fail "no nameserver found in $RESOLV_CONF"
printf 'resolver %svalid=10s ipv6=off;\n' "$NS" > "$CONF_DIR/00-resolver.conf"

TMP="$(mktemp "$HTML_DIR/.config.XXXXXX")"
jq -n \
  --arg clientId "$AUTH_CLIENT_ID" \
  --arg authority "$AUTH_AUTHORITY" \
  --arg scopes "$AUTH_API_SCOPE" \
  --arg known "$AUTH_KNOWN_AUTHORITIES" \
  --arg redirectUri "$AUTH_REDIRECT_URI" \
  --arg apiBaseUrl "$API_BASE_URL" \
  --arg appTitle "$APP_TITLE" \
  --arg appTagline "$APP_TAGLINE" \
  --arg retentionDays "$APP_RETENTION_DAYS" \
  --arg supportContact "$APP_SUPPORT_CONTACT" \
  --argjson guestEnabled "$AUTH_GUEST_ENABLED" \
  --arg guestLabel "$AUTH_GUEST_LABEL" \
  --arg guestSessionHours "$APP_GUEST_SESSION_HOURS" \
  --arg guestRetentionHours "$APP_GUEST_RETENTION_HOURS" \
  --argjson providers "$AUTH_PROVIDERS" \
  '{
     auth: {
       clientId: $clientId,
       authority: $authority,
       knownAuthorities: ($known | split(",") | map(gsub("^\\s+|\\s+$"; "")) | map(select(length > 0))),
       redirectUri: $redirectUri,
       apiScopes: ($scopes | split(",") | map(gsub("^\\s+|\\s+$"; "")) | map(select(length > 0))),
       providers: $providers,
       guest: { enabled: $guestEnabled, label: $guestLabel }
     },
     app: (
       { title: $appTitle, tagline: $appTagline }
       + (if $retentionDays != "" then { retentionDays: ($retentionDays | tonumber) } else {} end)
       + (if $supportContact != "" then { supportContact: $supportContact } else {} end)
       + (if $guestSessionHours != "" then { guestSessionHours: ($guestSessionHours | tonumber) } else {} end)
       + (if $guestRetentionHours != "" then { guestRetentionHours: ($guestRetentionHours | tonumber) } else {} end)
     ),
     apiBaseUrl: $apiBaseUrl
   }' > "$TMP"
chmod 644 "$TMP"
mv "$TMP" "$HTML_DIR/config.json"

# /data-deletion.html: single pass over the three placeholders, values HTML-escaped (& < > " ').
PAGE="$HTML_DIR/data-deletion.html"
[ -f "$PAGE" ] || fail "$PAGE not found"
TMP_PAGE="$(mktemp "$HTML_DIR/.page.XXXXXX")"
jq -Rs -j \
  --arg title "$APP_TITLE" \
  --arg days "$APP_RETENTION_DAYS" \
  --arg contact "$APP_SUPPORT_CONTACT" \
  'def esc: gsub("&"; "&amp;") | gsub("<"; "&lt;") | gsub(">"; "&gt;") | gsub("\""; "&quot;") | gsub("\u0027"; "&#39;");
   {
     "APP_TITLE": ($title | esc),
     "RETENTION_TEXT": (if $days != "" then "Chat data is also deleted automatically: each message \($days) days after it was sent, and each conversation \($days) days after its last activity." else "Chat data is also deleted automatically after a retention period." end),
     "SUPPORT_CONTACT_TEXT": (if $contact != "" then "To have your sign-in account deleted as well, contact \($contact | esc)." else "To have your sign-in account deleted as well, contact the operator of this service." end)
   } as $m
   | gsub("__(?<k>APP_TITLE|RETENTION_TEXT|SUPPORT_CONTACT_TEXT)__"; $m[.k])' "$PAGE" > "$TMP_PAGE"
chmod 644 "$TMP_PAGE"
mv "$TMP_PAGE" "$PAGE"

echo "10-write-config: wrote $HTML_DIR/config.json, $PAGE and $CONF_DIR/00-resolver.conf"
