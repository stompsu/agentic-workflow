#!/bin/sh
# Runs before the nginx image's own entrypoint scripts (20-envsubst-on-templates.sh).
#  1. validates BACKEND_UPSTREAM (it is substituted into nginx config)
#  2. writes the resolver include (DNS servers from resolv.conf)
#  3. writes /config.json from environment variables, JSON-escaped via jq (no secrets are involved)
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
  --argjson providers "$AUTH_PROVIDERS" \
  '{
     auth: {
       clientId: $clientId,
       authority: $authority,
       knownAuthorities: ($known | split(",") | map(gsub("^\\s+|\\s+$"; "")) | map(select(length > 0))),
       redirectUri: $redirectUri,
       apiScopes: ($scopes | split(",") | map(gsub("^\\s+|\\s+$"; "")) | map(select(length > 0))),
       providers: $providers
     },
     app: { title: $appTitle, tagline: $appTagline },
     apiBaseUrl: $apiBaseUrl
   }' > "$TMP"
chmod 644 "$TMP"
mv "$TMP" "$HTML_DIR/config.json"
echo "10-write-config: wrote $HTML_DIR/config.json and $CONF_DIR/00-resolver.conf"
