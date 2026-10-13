$ErrorActionPreference = 'Stop'
$publicationRoot = Split-Path -Parent $PSScriptRoot
$publicationPatterns = [ordered]@{
    'hardcoded_drive_path' = '(?<![A-Za-z0-9])[A-Za-z]:(?:\\|/(?!/))'
    'api_or_github_key' = '\b(?:sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b'
    'jwt' = '\beyJ[A-Za-z0-9_-]{30,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b'
    'private_key' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
    'email' = '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'
}
$publicationFindings = @()
$publicationFiles = @(Get-ChildItem -LiteralPath $publicationRoot -Recurse -File -Force | Where-Object {
    $_.FullName -notmatch '[\\/]\.git[\\/]|[\\/]test-results[\\/]'
})
foreach ($publicationFile in $publicationFiles) {
    $publicationRelative = $publicationFile.FullName.Substring($publicationRoot.Length + 1).Replace('\', '/')
    if ($publicationFile.Name -match '^(?:auth|status|follower-status|rate-preferences)\.json$|^-?\.env(?:\.|$)|-live|verification\.json$|^quota-startup-' -or
        $publicationFile.Extension -in '.jsonl', '.log', '.zip', '.pdb') {
        $publicationFindings += [pscustomobject]@{file=$publicationRelative; kind='private_runtime_or_unreviewed_archive'}
    }
    if ($publicationFile.Extension -in '.exe', '.png') {
        $publicationBytes = [IO.File]::ReadAllBytes($publicationFile.FullName)
        $publicationContents = [Text.Encoding]::ASCII.GetString($publicationBytes) + "`n" + [Text.Encoding]::Unicode.GetString($publicationBytes)
    } else {
        $publicationContents = [IO.File]::ReadAllText($publicationFile.FullName)
    }
    foreach ($publicationPattern in $publicationPatterns.Keys) {
        if ([regex]::IsMatch($publicationContents, $publicationPatterns[$publicationPattern])) {
            # Do not print the matching credential or other private value.
            $publicationFindings += [pscustomobject]@{file=$publicationRelative; kind=$publicationPattern}
        }
    }
}
[pscustomobject]@{passed=($publicationFindings.Count -eq 0);filesChecked=$publicationFiles.Count;findings=$publicationFindings} | ConvertTo-Json -Depth 4
if ($publicationFindings.Count -gt 0) { exit 1 }
