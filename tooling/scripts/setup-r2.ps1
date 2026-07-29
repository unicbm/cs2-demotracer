param(
    [string]$Bucket = "cs2-demotracer-releases",
    [string]$Domain = "releases.detr.site",
    [Parameter(Mandatory = $true)]
    [string]$ZoneId
)

$ErrorActionPreference = "Stop"

function Invoke-Wrangler([string[]]$Arguments, [switch]$AllowFailure) {
    $output = & npx.cmd --yes wrangler@latest @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "Wrangler failed with exit code $exitCode"
    }
    return $exitCode
}

Invoke-Wrangler -Arguments @("whoami")
$bucketStatus = Invoke-Wrangler -Arguments @("r2", "bucket", "info", $Bucket) -AllowFailure
if ($bucketStatus -ne 0) {
    Invoke-Wrangler -Arguments @("r2", "bucket", "create", $Bucket, "--storage-class=Standard")
}

$domainStatus = Invoke-Wrangler -Arguments @("r2", "bucket", "domain", "get", $Bucket, "--domain=$Domain") -AllowFailure
if ($domainStatus -ne 0) {
    Invoke-Wrangler -Arguments @(
        "r2", "bucket", "domain", "add", $Bucket,
        "--domain=$Domain",
        "--zone-id=$ZoneId",
        "--min-tls=1.2",
        "--force"
    )
}
Invoke-Wrangler -Arguments @("r2", "bucket", "domain", "get", $Bucket, "--domain=$Domain")
