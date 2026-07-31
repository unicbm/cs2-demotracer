# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

$insideWorkTree = & git -C $RepoRoot rev-parse --is-inside-work-tree 2>&1
if ($LASTEXITCODE -ne 0 -or "$insideWorkTree".Trim() -ne "true") {
    throw "Release packaging requires a Git worktree: $RepoRoot"
}

$status = @(& git -C $RepoRoot status --porcelain=v1 --untracked-files=all 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect Git worktree state: $($status -join [Environment]::NewLine)"
}
if ($status.Count -gt 0) {
    throw @"
Release packaging requires a clean Git worktree.
Commit or remove every tracked and untracked change, then rebuild.
$($status -join [Environment]::NewLine)
"@
}

$commit = (& git -C $RepoRoot rev-parse --verify HEAD 2>&1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
    throw "Release packaging requires a valid Git HEAD commit."
}

Write-Host "Clean release source: $commit"
