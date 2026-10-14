[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Initialize', 'Verify', 'Final', 'RecordReview')][string]$Mode,
    [ValidateRange(0, 4)][int]$Stage = 0,
    [string]$AuditDir,
    [string]$SnapshotSha256,
    [string]$ReportManifestSha256
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$auditRoot = [IO.Path]::GetFullPath((Get-Location).Path)
$assets = Join-Path $auditRoot '.specify/workflows/documentation-audit-assets'
$baselineLockPath = Join-Path $assets 'baseline.lock.json'
$reports = @('00-baseline-integrity.md', '01-requirements-consistency.md',
    '02-architecture-review.md', '03-task-dependencies.md', '04-ai-task-readiness.md',
    '05-consolidated-findings.md')

function Assert-PlainPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not ($absolute -eq $auditRoot -or $absolute.StartsWith($auditRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Percorso fuori dal progetto: $absolute"
    }
    $cursor = $absolute
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $entry = Get-Item -LiteralPath $cursor -Force
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Link/reparse point non ammesso: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
function Get-Digest([string]$Path) {
    Assert-PlainPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "File mancante: $Path" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Write-NewFile([string]$Path, [string]$Text) {
    Assert-PlainPath $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}
function Assert-PinnedBaseline {
    Assert-PlainPath $baselineLockPath
    $lock = Get-Content -LiteralPath $baselineLockPath -Raw | ConvertFrom-Json -AsHashtable
    if ($lock.schema_version -ne 1 -or $lock.files.Count -ne 7) { throw 'Baseline lock non valido.' }
    foreach ($relative in $lock.files.Keys) {
        if ((Get-Digest (Join-Path $auditRoot $relative)) -ne $lock.files[$relative]) {
            throw "Baseline diversa da quella validata: $relative. Non riproiettare o aggiornare automaticamente il lock."
        }
    }
    return $lock
}
function Get-ProjectSnapshot([string]$ExcludedAuditDir) {
    $result = [ordered]@{}
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($auditRoot)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in (Get-ChildItem -LiteralPath $directory -Force | Sort-Object Name)) {
            $relative = [IO.Path]::GetRelativePath($auditRoot, $entry.FullName).Replace('\', '/')
            # Only Git internals, engine checkpoints and THIS audit session are writable exclusions.
            if ($relative -eq '.git' -or $relative -eq '.specify/workflows/runs' -or $relative -eq $ExcludedAuditDir) { continue }
            Assert-PlainPath $entry.FullName
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            else { $result[$relative] = Get-Digest $entry.FullName }
        }
    }
    return $result
}
function Assert-Report([string]$Name) {
    $path = Join-Path $sessionPath $Name
    Assert-PlainPath $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Report mancante: $Name" }
    $body = Get-Content -LiteralPath $path -Raw -Encoding utf8
    if ([string]::IsNullOrWhiteSpace($body) -or $body -notmatch '(?m)^#\s+\S') {
        throw "Report vuoto o senza titolo Markdown: $Name"
    }
}
function Assert-UnchangedProject {
    if ($SnapshotSha256 -notmatch '^[a-f0-9]{64}$') { throw 'Digest dello snapshot mancante/non valido.' }
    $snapshotPath = Join-Path $sessionPath 'project-baseline.json'
    if ((Get-Digest $snapshotPath) -ne $SnapshotSha256) { throw 'Snapshot di controllo alterato.' }
    $snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json -AsHashtable
    $current = Get-ProjectSnapshot $AuditDir
    $changes = [Collections.Generic.List[string]]::new()
    foreach ($relative in $snapshot.files.Keys) {
        if (-not $current.Contains($relative)) { $changes.Add("REMOVED $relative") }
        elseif ($current[$relative] -ne $snapshot.files[$relative]) { $changes.Add("CHANGED $relative") }
    }
    foreach ($relative in $current.Keys) {
        if (-not $snapshot.files.ContainsKey($relative)) { $changes.Add("ADDED $relative") }
    }
    if ($changes.Count -gt 0) {
        $failurePath = Join-Path $sessionPath ('integrity-failure-' + [Guid]::NewGuid().ToString('N') + '.md')
        Write-NewFile $failurePath ("# Integrita FAIL`n`n" + ($changes -join "`n") + "`n`nNessun ripristino automatico eseguito.`n")
        throw ('Scritture fuori dal perimetro audit: ' + ($changes -join ', '))
    }
    $null = Assert-PinnedBaseline
}
function Assert-ReportsForStage {
    $lastIndex = switch ($Stage) { 0 { 0 } 1 { 1 } 2 { 2 } 3 { 4 } 4 { 5 } }
    foreach ($name in $reports[0..$lastIndex]) { Assert-Report $name }
    if ($Stage -ge 3) {
        $taskSource = Get-Content -LiteralPath (Join-Path $auditRoot 'AI-Project-Documentation-Tasks.md') -Raw
        $originalIds = @([regex]::Matches($taskSource, '(?m)^\|\s*(M\d+-\d+)\s*\|') | ForEach-Object { $_.Groups[1].Value })
        $readiness = Get-Content -LiteralPath (Join-Path $sessionPath '04-ai-task-readiness.md') -Raw
        $rows = @([regex]::Matches($readiness, '(?m)^\|\s*(M\d+-\d+)\s*\|[^\r\n]*') | ForEach-Object { $_.Value })
        $rowIds = @($rows | ForEach-Object { [regex]::Match($_, '^\|\s*(M\d+-\d+)').Groups[1].Value })
        if ($originalIds.Count -eq 0 -or $rows.Count -ne $originalIds.Count) { throw 'Readiness: numero di righe diverso dai task originali.' }
        foreach ($id in $originalIds) {
            if (@($rowIds | Where-Object { $_ -eq $id }).Count -ne 1) { throw "Readiness mancante/duplicata: $id" }
        }
        foreach ($row in $rows) {
            if ($row -notmatch '\|\s*(READY|CONDITIONAL|BLOCKED|UNVERIFIABLE)\s*\|') { throw "Readiness senza stato valido: $row" }
        }
    }
}

try {
    Assert-PlainPath $assets
    if ($Mode -eq 'Initialize') {
        $init = Get-Content -LiteralPath (Join-Path $auditRoot '.specify/init-options.json') -Raw | ConvertFrom-Json
        $integration = Get-Content -LiteralPath (Join-Path $auditRoot '.specify/integration.json') -Raw | ConvertFrom-Json
        $versionText = (& specify version 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $versionText -notmatch 'CLI Version\s+1\.2\.0\s') { throw 'Richiesta CLI Spec Kit 1.2.0.' }
        if ($init.speckit_version -ne '1.2.0' -or $integration.version -ne '1.2.0' -or 'codex' -notin $integration.installed_integrations) {
            throw 'Richiesto progetto Spec Kit 1.2.0 con integrazione Codex installata.'
        }
        $null = Get-Command codex -ErrorAction Stop
        $lock = Assert-PinnedBaseline
        $AuditDir = 'audits/documentation-audit/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N')
        $sessionPath = Join-Path $auditRoot $AuditDir
        Assert-PlainPath $sessionPath
        $files = Get-ProjectSnapshot $AuditDir
        $null = New-Item -ItemType Directory -Path $sessionPath
        $snapshotPath = Join-Path $sessionPath 'project-baseline.json'
        Write-NewFile $snapshotPath (([ordered]@{ schema_version = 1; created_utc = [DateTime]::UtcNow.ToString('o'); files = $files;
            exclusions = @('.git', '.specify/workflows/runs', $AuditDir) }) | ConvertTo-Json -Depth 8)
        $digest = Get-Digest $snapshotPath
        $lines = @('# Baseline integrity', '', 'Esito: PASS', '', "Sessione: $AuditDir", '',
            'Hash SHA-256 dei byte (encoding e terminatori inclusi). Lock della baseline validata verificato.', '',
            '| Percorso | SHA-256 |', '|---|---|')
        foreach ($relative in ($lock.files.Keys | Sort-Object)) { $lines += "| $relative | $($lock.files[$relative]) |" }
        $lines += @('', "File protetti nello snapshot: $($files.Count).", "Snapshot SHA-256: $digest", '',
            'Esclusioni: internals Git, checkpoint del motore e sola directory di questa sessione.',
            'I controlli rilevano modifiche; non costituiscono una sandbox e non ripristinano file.',
            'La costituzione viene preservata, senza considerarne i placeholder principi adottati.')
        Write-NewFile (Join-Path $sessionPath $reports[0]) ($lines -join "`n")
        [ordered]@{ audit_dir = $AuditDir; snapshot_sha256 = $digest } | ConvertTo-Json -Compress
        exit 0
    }
    if ($AuditDir -notmatch '^audits/documentation-audit/\d{8}T\d{6}Z-[a-f0-9]{32}$') { throw 'Directory audit non valida.' }
    $sessionPath = Join-Path $auditRoot $AuditDir
    Assert-PlainPath $sessionPath
    Assert-UnchangedProject
    Assert-ReportsForStage
    if ($Mode -eq 'Final') {
        if ($Stage -ne 4) { throw 'La verifica finale richiede tutti i report.' }
        $checkPath = Join-Path $sessionPath '06-final-checks.md'
        if (-not (Test-Path -LiteralPath $checkPath)) {
            $lines = @('# Final checks', '', 'Esito meccanico: PASS', '', "UTC: $([DateTime]::UtcNow.ToString('o'))", '',
                'Baseline e altri file protetti invariati; nessun file aggiunto/rimosso fuori dalle esclusioni.',
                'Presenti i report 00-05 con titolo e contenuto; matrice readiness completa per i task originali.', '',
                '| Report | SHA-256 |', '|---|---|')
            foreach ($name in $reports) { $lines += "| $name | $(Get-Digest (Join-Path $sessionPath $name)) |" }
            $lines += @('', 'Questa verifica non certifica correttezza semantica, compatibilita runtime o fattibilita.',
                'Gate di revisione umana ancora pendente. Nessuna implementazione autorizzata.')
            Write-NewFile $checkPath ($lines -join "`n")
        }
        Assert-Report '06-final-checks.md'
        $manifestPath = Join-Path $sessionPath 'report-manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) {
            $manifest = [ordered]@{}
            foreach ($name in @($reports) + @('06-final-checks.md')) { $manifest[$name] = Get-Digest (Join-Path $sessionPath $name) }
            Write-NewFile $manifestPath ($manifest | ConvertTo-Json)
        }
        # On resume, also reject an existing manifest inconsistent with its reports.
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($name in @($reports) + @('06-final-checks.md')) {
            if ($manifest[$name] -ne (Get-Digest (Join-Path $sessionPath $name))) { throw "Report alterato: $name" }
        }
        [ordered]@{ report_manifest_sha256 = Get-Digest $manifestPath } | ConvertTo-Json -Compress
    } elseif ($Mode -eq 'RecordReview') {
        if ($ReportManifestSha256 -notmatch '^[a-f0-9]{64}$') { throw 'Digest manifest report mancante.' }
        $manifestPath = Join-Path $sessionPath 'report-manifest.json'
        if ((Get-Digest $manifestPath) -ne $ReportManifestSha256) { throw 'Manifest report alterato durante la revisione.' }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($name in @($reports) + @('06-final-checks.md')) {
            if ($manifest[$name] -ne (Get-Digest (Join-Path $sessionPath $name))) { throw "Report alterato durante la revisione: $name" }
        }
        $reviewPath = Join-Path $sessionPath '07-human-review.md'
        if (-not (Test-Path -LiteralPath $reviewPath)) {
            Write-NewFile $reviewPath ("# Human review`n`nEsito: approve, acquisito dal gate del motore.`n`nUTC: $([DateTime]::UtcNow.ToString('o'))`n`nBaseline, perimetro protetto e hash di tutti i report 00-06 ricontrollati: PASS.`n`nL'accettazione riguarda esclusivamente l'audit. Nessuna modifica, ADR o implementazione autorizzata.`n`nIl gate non autentica l'identita del revisore; la scelta e registrata anche nei checkpoint del motore.`n")
        }
        Assert-Report '07-human-review.md'
        Write-Output 'PASS: revisione registrata e integrita finale verificata.'
    } else { Write-Output 'PASS: report richiesti presenti; baseline e perimetro protetto invariati.' }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
