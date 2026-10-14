#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
rm -rf artifacts/publish
rm -f artifacts/lambda.zip
mkdir -p artifacts
# Framework-dependent .NET 10 assembly: no native AOT or platform-specific binaries.
dotnet publish src/TechChallenge.Lambda -c Release --no-restore -o artifacts/publish
(cd artifacts/publish && zip -qr ../lambda.zip .)
