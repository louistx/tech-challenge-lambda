#!/usr/bin/env bash
set -euo pipefail
base="${1%/}"
curl --fail --silent --show-error --retry 10 --retry-delay 1 --retry-connrefused --retry-all-errors "$base/.well-known/jwks.json" | jq -e '.keys | length > 0' >/dev/null
curl --fail --silent --show-error --retry 10 --retry-delay 1 --retry-connrefused --retry-all-errors "$base/.well-known/openid-configuration" | jq -e '.issuer and .jwks_uri' >/dev/null
status=$(curl --silent --output /dev/null --write-out '%{http_code}' "$base/auth/me")
test "$status" = 401
echo 'Smoke aprovado: JWKS, discovery e rejeição de acesso anônimo.'
