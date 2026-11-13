#!/usr/bin/env pwsh
# scripts/ci-checks.ps1
# PoRedoMedia's extra build-time checks, run by the shared deploy.yml after the unit tests, from the
# repo root with the solution already built in Release. Exits non-zero on any failure.
#   - Test budgets per suite (scripts/check-test-budgets.ps1: Unit 100 / Integration 50 / E2EAPI 25 / E2EUI 25).
# Integration and E2E suites stay local by choice (no containers, no ffmpeg in CI).

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

& ./scripts/check-test-budgets.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
