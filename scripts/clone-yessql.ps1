# Clones a YesSql release into ./external so the conformance projects can source-link YesSql's own test suite
# (CoreTests) and run it against the Cosmos provider. Idempotent.
#
#   scripts/clone-yessql.ps1                                   # v5.4.7 into external/yessql
#   scripts/clone-yessql.ps1 -Tag v6.0.0 -Directory yessql6    # v6.0.0 into external/yessql6
param(
    [string]$Tag = 'v5.4.7',
    [string]$Directory = 'yessql'
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot "..\external\$Directory"
if (Test-Path (Join-Path $target 'test\YesSql.Tests\CoreTests.cs')) {
    Write-Host "external/$Directory already present."
    exit 0
}
Write-Host "Cloning YesSql $Tag into external/$Directory ..."
git clone --depth 1 --branch $Tag https://github.com/sebastienros/yessql $target
