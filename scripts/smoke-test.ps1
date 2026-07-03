# End-to-end smoke test against a running Loadstone instance.
#
# Drives the real pipeline with the sample dataset: XML, JSON, and zip-of-CSV imports,
# an idempotent re-import, and a file with bad rows to exercise the rejection store.
# Requires only the API to be reachable; database state is asserted through the API.
#
# Usage:
#   .\scripts\smoke-test.ps1 [-BaseUrl http://localhost:8080]
param(
    [string]$BaseUrl = "http://localhost:8080"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$failures = 0

function Assert-True($condition, $message) {
    if ($condition) {
        Write-Host "  PASS  $message" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $message" -ForegroundColor Red
        $script:failures++
    }
}

function Import-File($path, $expectStatus) {
    $response = curl.exe -s -X POST "$BaseUrl/api/datasets/orders/imports" -F "file=@$path" | ConvertFrom-Json
    if (-not $response.jobId) { throw "Upload of $path was not accepted: $response" }
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $job = Invoke-RestMethod "$BaseUrl/api/imports/$($response.jobId)"
    } while ($job.status -in @("Pending", "Processing") -and (Get-Date) -lt $deadline)
    Assert-True ($job.status -eq $expectStatus) "$(Split-Path $path -Leaf): status $($job.status) (expected $expectStatus)"
    return $job
}

Write-Host "Loadstone smoke test against $BaseUrl"

# 0. Health and dataset registration
$health = Invoke-RestMethod "$BaseUrl/health"
Assert-True ($health -eq "Healthy") "health endpoint reports Healthy"
$datasets = @(Invoke-RestMethod "$BaseUrl/api/datasets")
Assert-True ($datasets.name -contains "orders") "sample dataset 'orders' is registered"

# 1. XML import
$xml = Import-File "$repoRoot\samples\orders\orders.xml" "Succeeded"
Assert-True ($xml.recordsRead -eq 6) "XML: 6 records read"
Assert-True ($xml.rowsInserted + $xml.rowsUpdated -eq 6) "XML: 6 rows written"

# 2. Idempotent re-import: same file must update, never duplicate
$rerun = Import-File "$repoRoot\samples\orders\orders.xml" "Succeeded"
Assert-True ($rerun.rowsInserted -eq 0) "re-import inserts nothing"
Assert-True ($rerun.rowsUpdated -eq 6) "re-import updates all 6 rows"

# 3. JSON import
$json = Import-File "$repoRoot\samples\orders\orders.json" "Succeeded"
Assert-True ($json.recordsRead -eq 5) "JSON: 5 records read"

# 4. Hierarchical CSV (zip)
$zip = Join-Path $env:TEMP "loadstone-smoke-orders.zip"
Compress-Archive -Path "$repoRoot\samples\orders\csv\*" -DestinationPath $zip -Force
$csv = Import-File $zip "Succeeded"
Assert-True ($csv.recordsRead -eq 5) "CSV zip: 5 records read"
Remove-Item $zip -Force

# 5. Bad rows are quarantined, good rows import
$badFile = Join-Path $env:TEMP "loadstone-smoke-bad.xml"
@"
<?xml version="1.0" encoding="utf-8"?>
<Orders>
  <Order><OrderNumber>BAD-1</OrderNumber><Total>banana</Total></Order>
  <Order><Total>50.00</Total></Order>
  <Order><OrderNumber>SMOKE-GOOD</OrderNumber><Country>DK</Country><Total>75.00</Total></Order>
</Orders>
"@ | Out-File $badFile -Encoding utf8
$bad = Import-File $badFile "CompletedWithRejections"
Assert-True ($bad.recordsRejected -eq 2) "bad file: 2 records rejected"
Assert-True ($bad.rowsInserted + $bad.rowsUpdated -eq 1) "bad file: the good record still imported"
$rejections = @(Invoke-RestMethod "$BaseUrl/api/imports/$($bad.id)/rejections")
Assert-True ($rejections.Count -eq 2) "rejection report has 2 rows"
Assert-True ($rejections | Where-Object { $_.field -eq "Total" -and $_.rawValue -eq "banana" }) "rejection carries field and raw value"
Assert-True (-not ($rejections | Where-Object { -not $_.sourceLine })) "every rejection carries a source line"
Remove-Item $badFile -Force

# 6. Lookup auto-creation produced the code list
$lists = @(Invoke-RestMethod "$BaseUrl/api/codelists")
$countries = $lists | Where-Object { $_.name -eq "countries" }
Assert-True ($null -ne $countries -and $countries.codeCount -ge 5) "code list 'countries' auto-created with the sample codes"

Write-Host ""
if ($failures -eq 0) {
    Write-Host "Smoke test passed." -ForegroundColor Green
    exit 0
}

Write-Host "$failures check(s) failed." -ForegroundColor Red
exit 1
