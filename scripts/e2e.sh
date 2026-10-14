#!/usr/bin/env bash
set -euo pipefail
# Only fictional data and loopback services. Never print OTPs or tokens.
base=http://localhost:8080
status=$(curl -s -o /dev/null -w '%{http_code}' -H 'content-type: application/json' -d '{"cpf":"00000000000"}' "$base/auth/login")
test "$status" = 400
challenge=$(curl -fsS -H 'content-type: application/json' -d '{"cpf":"52998224725"}' "$base/auth/login" | jq -er .challenge_id)
message=$(curl -fsS http://localhost:8025/api/v1/messages | jq -er '[.messages[] | select(any(.To[]; .Address == "cliente@example.test"))][0].ID')
code=$(curl -fsS "http://localhost:8025/api/v1/message/$message" | jq -er '.Text | capture("(?<otp>[0-9]{6})").otp')
payload=$(jq -nc --arg id "$challenge" --arg code "$code" '{challenge_id:$id,code:$code}')
token=$(curl -fsS -H 'content-type: application/json' -d "$payload" "$base/auth/verify" | jq -er .access_token)
curl -fsS -H "Authorization: Bearer $token" "$base/auth/me" | jq -e '.sub and .scope == "customer"' >/dev/null
status=$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer ${token}tampered" "$base/auth/me")
test "$status" = 401
status=$(curl -s -o /dev/null -w '%{http_code}' -H 'content-type: application/json' -d "$payload" "$base/auth/verify")
test "$status" = 401
echo 'E2E aprovado: CPF, PostgreSQL, OTP no Mailpit, JWT, rota protegida e bloqueio de reuso.'
