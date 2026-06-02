# Clones the YesSql v5.4.7 source into ./external for the conformance project, which source-links
# YesSql's own test suite (CoreTests) and runs it against the Cosmos provider. Idempotent.
$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '..\external\yessql'
if (Test-Path (Join-Path $target 'test\YesSql.Tests\CoreTests.cs')) {
    Write-Host "external/yessql already present."
    exit 0
}
Write-Host "Cloning YesSql v5.4.7 into external/yessql ..."
git clone --depth 1 --branch v5.4.7 https://github.com/sebastienros/yessql $target
