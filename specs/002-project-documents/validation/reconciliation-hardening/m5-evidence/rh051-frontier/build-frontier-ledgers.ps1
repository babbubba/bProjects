$ErrorActionPreference = 'Stop'
$ev = "$HOME\bprojects-rh051-evidence\run4-resume-real\final"
$out = 'C:\source\repos\babbubba\bProjects\specs\002-project-documents\validation\reconciliation-hardening\m5-evidence\rh051-frontier'
New-Item -ItemType Directory -Force $out | Out-Null

$rows = Get-Content "$ev\work-outputs.jsonl" | Where-Object { $_ -like '{*' } | ForEach-Object { $_ | ConvertFrom-Json }
$led = Get-Content "$ev\batch-ledger.jsonl" | Where-Object { $_ -like '{*' } | ForEach-Object { $_ | ConvertFrom-Json } | Sort-Object Ordinal
for ($i = 0; $i -lt $rows.Count; $i++) { if ($rows[$i].BatchId -ne $led[$i].BatchId) { throw "ordine incoerente alla riga $i" } }

function PairKey($a, $b) { (@($a, $b) | Sort-Object { $_ }) -join '|' }

$cands = @{}
$batches = @()
$compared = @{}
$k = 0
foreach ($r in $rows) {
  $p = $r.PayloadJson; $o = $p.output; $ord = $led[$k].Ordinal; $k++
  $rec = [ordered]@{ ordinal = $ord; stage = $p.stage; view = $p.view; batchId = $r.BatchId; focus = $null; nCandidates = @($o.candidates).Count }
  foreach ($c in @($o.candidates)) {
    $isEE = ($c.left.kind -eq 'Evidence' -and $c.right.kind -eq 'Evidence')
    if (-not $cands.ContainsKey($c.candidateId)) {
      $cands[$c.candidateId] = [ordered]@{
        candidateId = $c.candidateId; shape = $(if ($isEE) { 'EvidenceEvidence' } else { 'Groups' }); type = $c.proposedType; view = $p.view; proposedBy = $ord; proposedByStage = $p.stage
        parent = $c.parentCandidateId; pair = $(if ($isEE) { PairKey $c.left.id $c.right.id } else { $null }); state = 'Pending'; reason = $null; processedAt = $null
      }
    }
  }
  if ($p.stage -eq 'compare') {
    $fc = $p.catalog.focusCandidateId; $d = @($o.dispositions)[0]
    $pair = PairKey $p.catalog.entries[0].evidenceId $p.catalog.entries[1].evidenceId
    $rec.focus = $fc
    if (-not $cands.ContainsKey($fc)) { $cands[$fc] = [ordered]@{ candidateId = $fc; shape = 'EvidenceEvidence'; type = $null; view = $p.view; proposedBy = $null; proposedByStage = 'server(directory/known-relation)'; parent = $null; pair = $pair; state = 'Pending'; reason = $null; processedAt = $null } }
    $cands[$fc].state = $d.state; $cands[$fc].reason = $d.reason; $cands[$fc].processedAt = $ord
    $compared[$pair] = @{ state = $d.state; ordinal = $ord; candidateId = $fc }
  }
  elseif ($p.stage -eq 'expand') {
    $fc = $p.catalog.focusCandidateId; $rec.focus = $fc
    if (-not $cands.ContainsKey($fc)) { $cands[$fc] = [ordered]@{ candidateId = $fc; shape = 'Groups'; type = $null; view = $p.view; proposedBy = $null; proposedByStage = 'server(directory)'; parent = $null; pair = $null; state = 'Pending'; reason = $null; processedAt = $null } }
    $cands[$fc].processedAt = $ord
    if (@($o.candidates).Count -ge 16) { $cands[$fc].state = 'NotProcessed'; $cands[$fc].reason = 'expansion_saturated (derivato: 16 candidati nel blocco)' } else { $cands[$fc].state = 'Expanded/Compared (derivato)' }
  }
  $batches += [pscustomobject]$rec
}

$exp = @(); $seenPair = @{}
foreach ($b in ($batches | Where-Object { $_.stage -eq 'expand' })) {
  $row = $rows[[array]::IndexOf(@($batches.ordinal), $b.ordinal)]
  $cs = @($row.PayloadJson.output.candidates)
  $pairs = @($cs | ForEach-Object { PairKey $_.left.id $_.right.id })
  $uniq = @($pairs | Select-Object -Unique)
  $dupInBatch = $pairs.Count - $uniq.Count
  $dupPrev = @($uniq | Where-Object { $seenPair.ContainsKey($_) }).Count
  $alreadyCompared = @($uniq | Where-Object { $compared.ContainsKey($_) }).Count
  $newNeeded = @($uniq | Where-Object { -not $seenPair.ContainsKey($_) -and -not $compared.ContainsKey($_) }).Count
  $ids = @($cs | ForEach-Object { $_.candidateId })
  foreach ($u in $uniq) { $seenPair[$u] = $true }
  $exp += [pscustomobject][ordered]@{
    ordinal = $b.ordinal; parent = $b.focus; view = $b.view; candidates = $cs.Count; atCap = ($cs.Count -ge 16); uniquePairs = $uniq.Count; duplicatePairsInBatch = $dupInBatch
    duplicatePairsAcrossEarlierExpansions = $dupPrev; pairsAlreadyComparedBeforeStop = $alreadyCompared; newComparesRequiredByThisBatch = $newNeeded
    distinctCandidateIds = @($ids | Select-Object -Unique).Count; parentDisposition = $(if ($cs.Count -ge 16) { 'NotProcessed:expansion_saturated' } else { 'Expanded/Compared' })
    types = (($cs | Group-Object proposedType | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ',')
  }
}
$exp | ForEach-Object { $_ | ConvertTo-Json -Compress } | Out-File "$out\expansions-ledger.jsonl" -Encoding utf8
"expand batch: $($exp.Count); at cap: $(@($exp | Where-Object atCap).Count); total candidati: $(($exp | Measure-Object candidates -Sum).Sum); coppie uniche globali: $($seenPair.Count)"
"duplicati in-batch: $(($exp | Measure-Object duplicatePairsInBatch -Sum).Sum); duplicati fra espansioni: $(($exp | Measure-Object duplicatePairsAcrossEarlierExpansions -Sum).Sum); gia' confrontate: $(($exp | Measure-Object pairsAlreadyComparedBeforeStop -Sum).Sum); coppie uniche non ancora confrontate: $(($seenPair.Keys | Where-Object { -not $compared.ContainsKey($_) }).Count)"
"candidati figli (con parent): $(@($cands.Values | Where-Object { $_.parent }).Count)"

"--- candidati per vista/forma/origine"
$cands.Values | Group-Object { "$($_.view)/$($_.shape)/$($_.proposedByStage)" } | Sort-Object Name | ForEach-Object { "$($_.Name) = $($_.Count)" }
"--- stati"
$cands.Values | Group-Object state | ForEach-Object { "$($_.Name) = $($_.Count)" }
$cands.Values | Sort-Object { $_.candidateId } | ForEach-Object { $_ | ConvertTo-Json -Compress } | Out-File "$out\candidates-ledger.jsonl" -Encoding utf8

$procA = @($batches | Where-Object { ($_.stage -in 'compare', 'expand') -and $_.view -eq 'A' } | ForEach-Object { $_.focus })
$firstBreak = -1; for ($i = 1; $i -lt $procA.Count; $i++) { if ([string]::CompareOrdinal($procA[$i - 1], $procA[$i]) -gt 0) { $firstBreak = $i; break } }
"elaborati in fase A: $($procA.Count); prima rottura dell'ordine ordinale per candidateId all'indice: $firstBreak"
"sequenza (c=compare, e=expand): " + (($batches | Where-Object { $_.stage -in 'compare', 'expand' } | ForEach-Object { $_.stage.Substring(0, 1) }) -join '')
"--- candidati dei modelli per vista di provenienza e forma, processati/pending (esclusi i figli)"
$cands.Values | Where-Object { -not $_.parent } | Group-Object { "$($_.view)/$($_.shape)" } | ForEach-Object {
  $g = $_.Group; "{0}: totale={1}; elaborati={2}; non elaborati={3}" -f $_.Name, $g.Count, @($g | Where-Object processedAt).Count, @($g | Where-Object { -not $_.processedAt }).Count
}
$batches | ForEach-Object { $_ | ConvertTo-Json -Compress } | Out-File "$out\batches-reconstructed.jsonl" -Encoding utf8

# --- frontiera esatta sui soli batch committati
$pendingModel = @($cands.Values | Where-Object { -not $_.parent -and $_.shape -eq 'EvidenceEvidence' -and -not $_.processedAt })
$pmA = @($pendingModel | Where-Object { $_.view -eq 'A' }); $pmB = @($pendingModel | Where-Object { $_.view -eq 'B' })
$childPairs = @($cands.Values | Where-Object { $_.parent } | ForEach-Object { $_.pair } | Select-Object -Unique)
$aPairs = @($pmA | ForEach-Object { $_.pair } | Select-Object -Unique)
$bPairs = @($pmB | ForEach-Object { $_.pair } | Select-Object -Unique)
"A E-E non elaborati: $($pmA.Count); di cui coppia gia' confrontata (cache hit senza chiamata): $(@($pmA | Where-Object { $compared.ContainsKey($_.pair) }).Count); coppie distinte: $($aPairs.Count)"
"B E-E (non iniziata): candidati $($pmB.Count); coppie distinte $($bPairs.Count); gia' confrontate in A: $(@($bPairs | Where-Object { $compared.ContainsKey($_) }).Count)"
"figli: coppie distinte $($childPairs.Count); gia' confrontate $(@($childPairs | Where-Object { $compared.ContainsKey($_) }).Count)"
$unionA = @($childPairs + $aPairs | Select-Object -Unique | Where-Object { -not $compared.ContainsKey($_) })
$unionAB = @($childPairs + $aPairs + $bPairs | Select-Object -Unique | Where-Object { -not $compared.ContainsKey($_) })
"confronti distinti ancora dovuti, fase A (figli + A pendenti): $($unionA.Count); con la fase B E-E: $($unionAB.Count)"
$perGroup = $batches | Where-Object { $_.stage -eq 'expand' } | Group-Object focus | ForEach-Object { "$($_.Name.Substring(0,8)):blocchi=$($_.Count)" }
"blocchi per candidato-gruppo A (committati): " + ($perGroup -join ' ')
$summary = [ordered]@{
  committedBatches = $batches.Count; compareBatches = @($batches | Where-Object stage -eq 'compare').Count; expandBatches = $exp.Count; expandAtCap = @($exp | Where-Object atCap).Count
  expansionCandidates = ($exp | Measure-Object candidates -Sum).Sum; expansionUniquePairs = $seenPair.Count; expansionDuplicateAcross = ($exp | Measure-Object duplicatePairsAcrossEarlierExpansions -Sum).Sum
  childPairsAlreadyCompared = @($childPairs | Where-Object { $compared.ContainsKey($_) }).Count
  pendingModelEE_A = $pmA.Count; pendingModelEE_A_cacheHit = @($pmA | Where-Object { $compared.ContainsKey($_.pair) }).Count; pendingModelEE_B = $pmB.Count
  distinctComparesOwedPhaseA = $unionA.Count; distinctComparesOwedPhaseAplusBEE = $unionAB.Count
  groupCandidatesNotStarted = @($cands.Values | Where-Object { -not $_.parent -and $_.shape -eq 'Groups' -and -not $_.processedAt }).Count
}
$summary | ConvertTo-Json | Out-File "$out\frontier-summary.json" -Encoding utf8
$unionAB | Sort-Object | Out-File "$out\owed-compare-pairs.txt" -Encoding utf8
