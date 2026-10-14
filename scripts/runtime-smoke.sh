#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
container=$(docker run --rm -d --platform linux/amd64 -p 127.0.0.1::8080 \
  -v "$PWD/artifacts/publish:/var/task:ro" public.ecr.aws/lambda/dotnet:10 \
  'TechChallenge.Lambda::TechChallenge.Lambda.Function::FunctionHandler')
trap 'docker rm -f "$container" >/dev/null 2>&1 || true' EXIT
port=$(docker port "$container" 8080/tcp | cut -d: -f2)
for attempt in $(seq 1 30); do
  if response=$(curl -fsS --max-time 10 \
    -H 'content-type: application/json' \
    -d '{"rawPath":"/runtime-smoke","requestContext":{"http":{"method":"GET","sourceIp":"127.0.0.1"}}}' \
    "http://127.0.0.1:$port/2015-03-31/functions/function/invocations" 2>/dev/null); then
    if jq -e '.statusCode == 404' <<< "$response" >/dev/null; then
      echo 'Runtime Lambda .NET 10: handler invocado com sucesso.'
      exit 0
    fi
    echo 'Runtime respondeu com erro; verificar o pacote e o handler.' >&2
    exit 1
  fi
  sleep 1
done
echo 'Runtime local não iniciou no prazo.' >&2
exit 1
