<#
.SYNOPSIS
    RH-001 (002 reconciliation-hardening): estrae le fixture di replay dagli archivi grezzi dei Run 2-5 (sola lettura) e scrive
    run{R}-{NNN}.json e PROVENANCE.json accanto allo script.

.DESCRIPTION
    Strumento ripetibile e versionato: stessi archivi, stessi byte. Le fixture contengono SOLO campi estratti dal corpo della richiesta
    (messaggio di sistema e messaggio utente), mai gli header ne' il percorso HTTP, piu' il testo grezzo dell'assistente e gli SHA-256 dei file d'archivio.
    Il corpus e' sintetico (T040, seed 20261106): nessun dato cliente.

    Controlli bloccanti (STOP del task: nessuna ricostruzione a mano):
      - sessione o file mancante;
      - SHA-256 del testo dell'assistente estratto diverso da quello del file d'archivio;
      - messaggio utente che non si lascia riscrivere identico dai campi estratti (round-trip del formato di ReconcileAliasMessages);
      - presenza di Bearer, Authorization, api_key, dell'host dell'endpoint o di /v1/ nell'output.

.PARAMETER ArchiveRoot
    Radice degli archivi (default %TEMP%\bprojects-diag).

.PARAMETER OutputDir
    Cartella di destinazione (default: la cartella dello script).
#>
[CmdletBinding()]
param(
    [string]$ArchiveRoot = (Join-Path $env:TEMP 'bprojects-diag'),
    [string]$OutputDir = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Sessioni d'archivio (assessment §1): timestamp e suffisso identificano la cartella; le chiamate sono quelle della gerarchia (034 file in totale).
$sessions = @(
    [pscustomobject]@{ Run = 2; Stamp = '20261110-214153'; Suffix = '6e615ff2'; First = 22; Last = 26 }
    [pscustomobject]@{ Run = 3; Stamp = '20261111-160309'; Suffix = '48872147'; First = 22; Last = 26 }
    [pscustomobject]@{ Run = 4; Stamp = '20261111-234029'; Suffix = 'b369e4e7'; First = 22; Last = 36 }
    [pscustomobject]@{ Run = 5; Stamp = '20261113-093355'; Suffix = 'eb600542'; First = 22; Last = 30 }
)

$stageOf = @{ 'foglia' = 'leaf'; 'riduzione' = 'reduce'; 'cross' = 'cross'; 'espansione' = 'expand'; 'confronto' = 'compare' }
$utf8 = [System.Text.UTF8Encoding]::new($false)
$trailer = "`nRispondi ora con l'unico oggetto JSON ``reconcile-alias-1``."
$itemsHeader = "Item gi$([char]0x00E0) proposti per le evidenze di questo batch:`n"
$relationsHeader = "Relazioni note fra evidenze di questo batch:`n"
$focusTailPlain = 'Risolvilo con le voci integrali sotto.'
$focusTailGroups = "Ogni voce indica il gruppo di appartenenza (gruppo=...): cerca collegamenti fra una voce di un gruppo e una dell'altro."

function Get-Sha256([byte[]]$bytes) {
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [System.Convert]::ToHexString($hash).ToLowerInvariant()
}

function Fail([string]$message) { throw "RH-001 STOP: $message" }

function Read-Match([string]$text, [string]$pattern, [int]$at) {
    $m = [regex]::new($pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline).Match($text, $at)
    if ($m.Success -and $m.Index -eq $at) { return $m }
    return $null
}

function ConvertFrom-ReconcileMessage([string]$user, [string]$where) {
    $head = Read-Match $user '\GTipo di batch: (?<t>[^\n]+)\nVista: (?<v>[^\n]+)\n' 0
    if ($null -eq $head) { Fail "$where`: intestazione 'Tipo di batch/Vista' non riconosciuta." }
    $label = $head.Groups['t'].Value
    if (-not $stageOf.ContainsKey($label)) { Fail "$where`: tipo di batch sconosciuto '$label'." }
    $at = $head.Length

    $focus = $null
    $focusFormat = $null
    $m = Read-Match $user '\GCandidato in esame: (?<type>\w+) fra \[(?<l>[EN][1-9][0-9]*)\] e \[(?<r>[EN][1-9][0-9]*)\] \(motivo: (?<why>[^\n]*)\)\. (?<tail>[^\n]*)\n' $at
    if ($null -ne $m) {
        $focusFormat = switch ($m.Groups['tail'].Value) {
            $focusTailPlain { 'plain' }
            $focusTailGroups { 'groups' }
            default { Fail "$where`: coda del candidato in esame non riconosciuta." }
        }
        $focus = [ordered]@{ type = $m.Groups['type'].Value; left = $m.Groups['l'].Value; right = $m.Groups['r'].Value; rationale = $m.Groups['why'].Value }
        $at += $m.Length
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    $groups = $null
    while ($true) {
        $m = Read-Match $user '\G<<<VOCE \[(?<a>E[1-9][0-9]*)\] origine=(?<o>\w+) tipo=(?<k>\w+) polarita=(?<p>\w+)(?: gruppo=(?<g>N[1-9][0-9]*(?:,N[1-9][0-9]*)?))?>>>\n(?<st>.*?)\n(?=<<<FONTE \[)' $at
        if ($null -eq $m) { break }
        $at += $m.Length
        if ($m.Groups['g'].Success) {
            if ($null -eq $groups) { $groups = [ordered]@{} }
            $groups[$m.Groups['a'].Value] = $m.Groups['g'].Value
        }
        elseif ($null -ne $groups) { Fail "$where`: voce $($m.Groups['a'].Value) senza gruppo in un batch con membership." }

        $sources = [System.Collections.Generic.List[object]]::new()
        while ($true) {
            $s = Read-Match $user '\G<<<FONTE \[(?<s>S[1-9][0-9]*)\] ruolo=(?<r>\w+)>>>\n(?<q>.*?)\n<<<FINE FONTE>>>\n' $at
            if ($null -eq $s) { break }
            $at += $s.Length
            $sources.Add([ordered]@{ alias = $s.Groups['s'].Value; role = $s.Groups['r'].Value; quote = $s.Groups['q'].Value })
        }

        $end = Read-Match $user '\G<<<FINE VOCE>>>\n' $at
        if ($null -eq $end) { Fail "$where`: voce $($m.Groups['a'].Value) senza chiusura." }
        $at += $end.Length
        $entries.Add([ordered]@{
                alias = $m.Groups['a'].Value; origin = $m.Groups['o'].Value; kind = $m.Groups['k'].Value; polarity = $m.Groups['p'].Value
                statement = $m.Groups['st'].Value; sources = $sources.ToArray()
            })
    }

    $nodes = [System.Collections.Generic.List[object]]::new()
    while ($true) {
        $m = Read-Match $user '\G<<<NODO \[(?<a>N[1-9][0-9]*)\] vista=(?<v>\w+)>>>\n(?<d>.*?)\n<<<FINE NODO>>>\n' $at
        if ($null -eq $m) { break }
        $at += $m.Length
        $nodes.Add([ordered]@{ alias = $m.Groups['a'].Value; view = $m.Groups['v'].Value; descriptor = $m.Groups['d'].Value })
    }

    if (-not $user.EndsWith($trailer, [System.StringComparison]::Ordinal)) { Fail "$where`: chiusura del messaggio non riconosciuta." }
    $block = $user.Substring($at, $user.Length - $trailer.Length - $at)

    $itemsText = ''
    $relationsText = ''
    $relationsAt = $block.IndexOf($relationsHeader, [System.StringComparison]::Ordinal)
    if ($relationsAt -ge 0) {
        $relationsText = $block.Substring($relationsAt + $relationsHeader.Length)
        $block = $block.Substring(0, $relationsAt)
    }

    if ($block.Length -gt 0) {
        if (-not $block.StartsWith($itemsHeader, [System.StringComparison]::Ordinal)) { Fail "$where`: blocco inatteso prima delle relazioni note." }
        $itemsText = $block.Substring($itemsHeader.Length)
    }

    $items = [System.Collections.Generic.List[object]]::new()
    if ($itemsText.Length -gt 0) {
        if (-not $itemsText.EndsWith("`n")) { Fail "$where`: item gia' proposti senza a capo finale." }
        $current = $null
        foreach ($line in $itemsText.Substring(0, $itemsText.Length - 1).Split("`n")) {
            $m = [regex]::Match($line, '^\[(?<h>i[1-9][0-9]*)\] \((?<e>E[1-9][0-9]*(?:, E[1-9][0-9]*)*)\) (?<t>.*)$')
            if ($m.Success) {
                $current = [ordered]@{ handle = $m.Groups['h'].Value; evidence = @($m.Groups['e'].Value -split ', '); text = $m.Groups['t'].Value }
                $items.Add($current)
            }
            elseif ($null -ne $current) { $current['text'] += "`n" + $line }   # testo su piu' righe: la riga di continuazione appartiene all'item precedente
            else { Fail "$where`: riga di item non riconosciuta." }
        }
    }

    $relations = [System.Collections.Generic.List[object]]::new()
    if ($relationsText.Length -gt 0) {
        if (-not $relationsText.EndsWith("`n")) { Fail "$where`: relazioni note senza a capo finale." }
        foreach ($line in $relationsText.Substring(0, $relationsText.Length - 1).Split("`n")) {
            $m = [regex]::Match($line, '^\[(?<a>R[1-9][0-9]*)\] (?<f>E[1-9][0-9]*) (?<t>\w+) (?<to>E[1-9][0-9]*) \(base=(?<b>\w+)\)$')
            if (-not $m.Success) { Fail "$where`: riga di relazione non riconosciuta." }
            $relations.Add([ordered]@{ alias = $m.Groups['a'].Value; from = $m.Groups['f'].Value; type = $m.Groups['t'].Value; to = $m.Groups['to'].Value; basis = $m.Groups['b'].Value })
        }
    }

    return [pscustomobject]@{
        Stage = $stageOf[$label]; View = $head.Groups['v'].Value; Focus = $focus; FocusFormat = $focusFormat; Groups = $groups
        Entries = $entries.ToArray(); Nodes = $nodes.ToArray(); Items = $items.ToArray(); Relations = $relations.ToArray()
    }
}

function ConvertTo-ReconcileMessage($f) {
    # Riscrittura speculare di ReconcileAliasMessages.Build: serve solo al round-trip di verifica.
    $labelOf = @{}
    foreach ($k in $stageOf.Keys) { $labelOf[$stageOf[$k]] = $k }
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.Append("Tipo di batch: $($labelOf[$f.Stage])`nVista: $($f.View)`n")
    if ($null -ne $f.Focus) {
        $tail = if ($null -eq $f.Groups) { $focusTailPlain } else { $focusTailGroups }
        [void]$sb.Append("Candidato in esame: $($f.Focus.type) fra [$($f.Focus.left)] e [$($f.Focus.right)] (motivo: $($f.Focus.rationale)). $tail`n")
    }

    foreach ($e in $f.Entries) {
        $membership = if ($null -eq $f.Groups) { '' } else { ' gruppo=' + $f.Groups[$e.alias] }
        [void]$sb.Append("<<<VOCE [$($e.alias)] origine=$($e.origin) tipo=$($e.kind) polarita=$($e.polarity)$membership>>>`n$($e.statement)`n")
        foreach ($s in $e.sources) { [void]$sb.Append("<<<FONTE [$($s.alias)] ruolo=$($s.role)>>>`n$($s.quote)`n<<<FINE FONTE>>>`n") }
        [void]$sb.Append("<<<FINE VOCE>>>`n")
    }

    foreach ($n in $f.Nodes) { [void]$sb.Append("<<<NODO [$($n.alias)] vista=$($n.view)>>>`n$($n.descriptor)`n<<<FINE NODO>>>`n") }
    if ($f.Items.Count -gt 0) {
        [void]$sb.Append($itemsHeader)
        foreach ($i in $f.Items) { [void]$sb.Append("[$($i.handle)] ($($i.evidence -join ', ')) $($i.text)`n") }
    }

    if ($f.Relations.Count -gt 0) {
        [void]$sb.Append($relationsHeader)
        foreach ($r in $f.Relations) { [void]$sb.Append("[$($r.alias)] $($r.from) $($r.type) $($r.to) (base=$($r.basis))`n") }
    }

    [void]$sb.Append($trailer)
    return $sb.ToString()
}

if (-not (Test-Path -LiteralPath $ArchiveRoot -PathType Container)) { Fail "archivio non trovato: $ArchiveRoot" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$provenanceFiles = [System.Collections.Generic.List[object]]::new()
$provenanceSessions = [System.Collections.Generic.List[object]]::new()
$endpointHosts = [System.Collections.Generic.HashSet[string]]::new()

foreach ($session in $sessions) {
    $found = @(Get-ChildItem -LiteralPath $ArchiveRoot -Directory | Where-Object { $_.Name.StartsWith($session.Stamp + '-', [System.StringComparison]::Ordinal) -and $_.Name.EndsWith('-' + $session.Suffix, [System.StringComparison]::Ordinal) })
    if ($found.Count -ne 1) { Fail "sessione del Run $($session.Run) ($($session.Stamp)-*-$($session.Suffix)) trovata $($found.Count) volte." }
    $dir = $found[0].FullName
    $configPath = Join-Path $dir 'config.json'
    if (Test-Path -LiteralPath $configPath) {
        $config = Get-Content -LiteralPath $configPath -Raw -Encoding utf8 | ConvertFrom-Json
        if ($null -ne $config.PSObject.Properties['endpointHost'] -and $config.endpointHost) { [void]$endpointHosts.Add([string]$config.endpointHost) }
    }

    $calls = [System.Collections.Generic.List[int]]::new()
    foreach ($call in $session.First..$session.Last) {
        $id = '{0:D3}' -f $call
        $requestPath = Join-Path $dir "$id-request.json"
        $assistantPath = Join-Path $dir "$id-assistant.txt"
        foreach ($p in @($requestPath, $assistantPath)) { if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { Fail "file mancante: $p" } }
        $where = "Run $($session.Run) chiamata $id"

        $requestBytes = [System.IO.File]::ReadAllBytes($requestPath)
        $assistantBytes = [System.IO.File]::ReadAllBytes($assistantPath)
        $requestSha = Get-Sha256 $requestBytes
        $assistantSha = Get-Sha256 $assistantBytes
        $assistant = $utf8.GetString($assistantBytes)
        if ((Get-Sha256 $utf8.GetBytes($assistant)) -ne $assistantSha) { Fail "$where`: il testo dell'assistente non e' riproducibile byte per byte (BOM o sequenze non UTF-8)." }

        $request = $utf8.GetString($requestBytes) | ConvertFrom-Json -Depth 50
        $messages = @($request.body.messages)
        if ($messages.Count -ne 2 -or $messages[0].role -ne 'system' -or $messages[1].role -ne 'user') { Fail "$where`: attesi un messaggio di sistema e uno utente." }
        $system = [string]$messages[0].content
        $user = [string]$messages[1].content

        $parsed = ConvertFrom-ReconcileMessage $user $where
        $fixture = [ordered]@{
            run = $session.Run
            call = $call
            session = $found[0].Name
            requestSha256 = $requestSha
            assistantSha256 = $assistantSha
            stage = $parsed.Stage
            view = $parsed.View
            system = $system
            focus = $parsed.Focus
            groups = $parsed.Groups
            entries = $parsed.Entries
            nodes = $parsed.Nodes
            items = $parsed.Items
            relations = $parsed.Relations
            assistant = $assistant
        }

        $rebuilt = ConvertTo-ReconcileMessage ([pscustomobject]@{
                Stage = $parsed.Stage; View = $parsed.View; Focus = $parsed.Focus; Groups = $parsed.Groups
                Entries = $parsed.Entries; Nodes = $parsed.Nodes; Items = $parsed.Items; Relations = $parsed.Relations
            })
        if (-not [string]::Equals($rebuilt, $user, [System.StringComparison]::Ordinal)) { Fail "$where`: il messaggio utente non si lascia riscrivere identico dai campi estratti." }

        $name = "run$($session.Run)-$id.json"
        $json = ($fixture | ConvertTo-Json -Depth 12 -EscapeHandling Default).Replace("`r`n", "`n") + "`n"
        $bytes = $utf8.GetBytes($json)
        [System.IO.File]::WriteAllBytes((Join-Path $OutputDir $name), $bytes)
        $provenanceFiles.Add([ordered]@{
                path = $name; run = $session.Run; call = $call; stage = $parsed.Stage; view = $parsed.View
                requestSha256 = $requestSha; assistantSha256 = $assistantSha; sha256 = (Get-Sha256 $bytes); bytes = $bytes.Length
            })
        $calls.Add($call)
    }

    $provenanceSessions.Add([ordered]@{ run = $session.Run; session = $found[0].Name; calls = $calls.ToArray() })
}

$provenance = [ordered]@{
    contract = 'reconcile-replay-provenance-1'
    declaration = 'corpus sintetico T040 (seed 20261106), nessun dato cliente'
    source = 'archivi grezzi locali %TEMP%\bprojects-diag (non versionati, sola lettura)'
    extractor = 'Extract-ReplayFixtures.ps1'
    extracted = 'solo dal corpo della richiesta (messaggio di sistema e messaggio utente) e dal testo dell''assistente; mai header ne'' percorso HTTP'
    task = '002 reconciliation-hardening RH-001'
    sessions = $provenanceSessions.ToArray()
    files = $provenanceFiles.ToArray()
}
$provenanceJson = ($provenance | ConvertTo-Json -Depth 12 -EscapeHandling Default).Replace("`r`n", "`n") + "`n"
[System.IO.File]::WriteAllBytes((Join-Path $OutputDir 'PROVENANCE.json'), $utf8.GetBytes($provenanceJson))

# Controllo finale: nessun segreto, host dell'endpoint o percorso HTTP nelle fixture scritte.
$forbidden = @('Bearer', 'Authorization', 'api_key', '/v1/') + @($endpointHosts)
foreach ($file in Get-ChildItem -LiteralPath $OutputDir -Filter '*.json') {
    $text = [System.IO.File]::ReadAllText($file.FullName, $utf8)
    foreach ($needle in $forbidden) {
        if ($text.Contains($needle, [System.StringComparison]::OrdinalIgnoreCase)) { Fail "$($file.Name) contiene '$needle'." }
    }
}

Write-Host "Scritte $($provenanceFiles.Count) fixture e PROVENANCE.json in $OutputDir"
