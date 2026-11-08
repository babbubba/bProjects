"""Summaries derived only from recorded ranks; no inference and no gold edits."""
import collections
import json
import math
from pathlib import Path
import statistics
from prepare import dump

HERE=Path(__file__).resolve().parent

def percentile(values,p): return sorted(values)[max(0,math.ceil(len(values)*p)-1)]

def main():
    aggregated=[]
    losses=[]
    lines=['# Misure retrieval','',
           'Hit: quota di query con almeno un gold. Recall: media per query delle evidenze gold distinte recuperate. '
           'Gold unici: unione delle evidenze recuperate in almeno una query pertinente; non è completezza per ogni domanda. '
           'Latenze: singola esecuzione per query, indice già preparato; p95 empirico nearest-rank, nessuna significatività statistica.', '',
           '| Corpus | Chunking | Metodo | Hit@1 | Hit@5 | Hit@10 | Recall@1 | Recall@5 | Recall@10 | Gold unici @10 | p50 ms | p95 ms |',
           '|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|']
    for path in sorted((HERE/'results').glob('*--*.json')):
        if path.name.endswith(('.partial.json','--error.json','--block_parent.json')): continue
        result=json.loads(path.read_text(encoding='utf-8'))
        for method in ['bm25','dense','hybrid_rrf','hybrid_rerank']:
            rows=[q['methods'][method] for q in result['queries']]
            valid=[r for r in rows if r['status']=='MEASURED']
            expected=set(result['indexable_gold'])
            found={id for row in valid for id,p in row['gold_ranks'].items() if p and p<=10}
            values={'scenario':result['scenario'],'variant':result['variant'],'method':method,'queries':len(rows),'measured':len(valid),'errors':len(rows)-len(valid),
                    'hit':{str(k):statistics.mean(r['hit'][str(k)] for r in rows) for k in [1,5,10,20,50]},
                    'recall':{str(k):statistics.mean(r['recall'][str(k)] for r in rows) for k in [1,5,10,20,50]},
                    'unique_gold_found_at_10':sorted(found),'unique_gold_expected':len(expected),'never_found_at_10':sorted(expected-found),
                    'not_indexable':sorted(id for id,yes in result['indexable_gold'].items() if not yes),
                    'first_correct_rank_mean':statistics.mean(r['first_correct_rank'] for r in valid if r['first_correct_rank']) if valid else None,
                    'latency_p50_ms':statistics.median(r['latency_ms'] for r in rows),'latency_p95_ms':percentile([r['latency_ms'] for r in rows],.95)}
            aggregated.append(values)
            x=values
            lines.append(f"| {x['scenario']} | {x['variant']} ({result['chunks']}) | {method} | "+' | '.join(f"{100*x[metric][str(k)]:.1f}%" for metric in ['hit','recall'] for k in [1,5,10])+f" | {len(found)}/{len(expected)} | {x['latency_p50_ms']:.1f} | {x['latency_p95_ms']:.1f} |")
            for query,row in zip(result['queries'],rows):
                if row['missing_at_10']: losses.append({'scenario':result['scenario'],'variant':result['variant'],'method':method,'query_id':query['id'],'missing_at_10':row['missing_at_10'],'gold_ranks':row['gold_ranks'],'status':row['status']})
    lines+=['','## Indicizzazione e reranking','','I tempi embedding indicano soltanto input nuovi: la cache per contenuto riusa vettori identici tra varianti. '
            'Le query della campagna principale vengono sempre inviate al modello. I risultati `pilot/` precedono questa correzione della misura di latenza e non entrano nella tabella.', '',
            '| Corpus | Variante | Build BM25 ms | Embedding nuovi | Chiamate embedding | Tempo HTTP embedding s | Token embedding | Query embedding p50 ms | Rerank p50 ms | Rerank p95 ms |',
            '|---|---|---:|---:|---:|---:|---:|---:|---:|---:|']
    for path in sorted((HERE/'results').glob('*--*.json')):
        if path.name.endswith(('.partial.json','--error.json','--block_parent.json')): continue
        r=json.loads(path.read_text(encoding='utf-8')); b=r['embedding_build']; qs=r['queries']
        lines.append(f"| {r['scenario']} | {r['variant']} | {r['bm25_build_ms']:.1f} | {b['new_unique_inputs']} | {b['http_calls']} | {b['http_ms']/1000:.2f} | {b['tokens']} | {statistics.median(q['query_embedding_http_ms'] for q in qs):.1f} | {statistics.median(q['reranking_ms'] for q in qs):.1f} | {percentile([q['reranking_ms'] for q in qs],.95):.1f} |")
    lines+=['','## Omissioni per query','','Vedere [missing-evidence.json](missing-evidence.json): include i gold mancanti dalla top 10, con posizione effettiva o assenza dai 50 candidati rerank. Nessun caso viene rimosso dal denominatore.']
    dump(HERE/'results/summary.json',aggregated)
    dump(HERE/'results/missing-evidence.json',losses)
    (HERE/'results/comparison.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print('Configurations',len(aggregated),'query-method omissions',len(losses))

if __name__=='__main__': main()
