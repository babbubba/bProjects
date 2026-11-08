"""Approved extra: original parser blocks -> existing chunk parents; no semantic graph."""
import json
import statistics
import time
from experiment import HERE,Http,Bm25,EMBED,RERANK,KS,metrics
from prepare import dump,sha
import lab

def build(scenario):
    chunks=[]; parents={}; folder=HERE/'corpus'/scenario
    for path in sorted(folder.iterdir()):
        if path.suffix not in ('.txt','.md','.docx') or path.name=='hashes.txt': continue
        version=sha(path.read_bytes())
        if path.name=='pm-message.txt':
            text=path.read_text(encoding='utf-8').strip(); key=path.name+'/message'
            parents[key]={'file':path.name,'text':text,'ranges':[]}
            chunks.append({'file':path.name,'text':text,'primary':text,'search':text,'heading':[],'source_type':'Message','version_sha256':version,'parent_keys':[key]})
            continue
        manifest=json.loads((HERE/'parsed'/scenario/(path.name+'.json')).read_text(encoding='utf-8'))
        blocks={b['ref']:b for b in manifest['blocks']}; linked={k:[] for k in blocks}
        for parent in manifest['chunks']:
            key=path.name+'/'+parent['chunkKey']
            primary=[r for r in parent['ranges'] if r['ownership']=='Primary']
            parents[key]={'file':path.name,'text':'\n'.join(blocks[r['blockRef']]['text'][r['start']:r['end']] for r in primary),'ranges':parent['ranges']}
            for r in primary:
                if key not in linked[r['blockRef']]: linked[r['blockRef']].append(key)
        for b in manifest['blocks']:
            if not b['text'].strip(): continue
            chunks.append({'file':path.name,'text':b['text'],'primary':b['text'],'search':'\n'.join(b.get('headingPath',[])+[b['text']]),
                           'heading':b.get('headingPath',[]),'source_type':'Document','version_sha256':version,'block_ref':b['ref'],'locator':b.get('locator'),
                           'parent_keys':linked[b['ref']]})
    for i,c in enumerate(chunks): c['id']=i
    return chunks,parents

def main():
    http=Http()
    cases=json.loads((HERE/'retrieval-cases.json').read_text(encoding='utf-8'))['scenarios']
    for scenario,qs in cases.items():
        output=HERE/'results'/(scenario+'--block_parent.json')
        if output.exists(): continue
        http.guard(); http.stage=scenario+'/block_parent/corpus'
        chunks,parents=build(scenario)
        expanded=[dict(c,primary='\n'.join(parents[k]['text'] for k in c['parent_keys'])) for c in chunks]
        dump(HERE/'indexes'/(scenario+'--block_parent.json'),{'children':chunks,'parents':parents})
        started=time.perf_counter(); bm=Bm25(chunks); bm_ms=(time.perf_counter()-started)*1000
        before=len(http.calls); started=time.perf_counter(); vectors,new=http.embed([c['search'] for c in chunks]); build_ms=(time.perf_counter()-started)*1000
        result={'scenario':scenario,'variant':'block_parent','chunks':len(chunks),'parents':len(parents),'case_file_sha256':sha((HERE/'retrieval-cases.json').read_bytes()),
                'index_sha256':sha((HERE/'indexes'/(scenario+'--block_parent.json')).read_bytes()),'bm25_build_ms':bm_ms,
                'embedding_build':{'wall_ms':build_ms,'new_unique_inputs':new,'http_calls':len(http.calls)-before,'http_ms':sum(c['milliseconds'] for c in http.calls[before:]),'tokens':sum((c.get('usage') or {}).get('prompt_tokens',0) for c in http.calls[before:])},'queries':[]}
        for q in qs:
            http.stage=scenario+'/block_parent/'+q['id']
            started=time.perf_counter(); a,sa=bm.rank(q['query']); t_bm=1000*(time.perf_counter()-started)
            started=time.perf_counter(); qv,_=http.embed([q['query']],use_cache=False); t_q=1000*(time.perf_counter()-started)
            started=time.perf_counter(); b,sb=lab.cos_rank(qv[0],vectors); t_dense=1000*(time.perf_counter()-started)
            started=time.perf_counter(); hybrid,sh=lab.rrf(a,b,len(chunks)); t_rrf=1000*(time.perf_counter()-started)
            candidates=hybrid[:50]; started=time.perf_counter()
            response=http.call('/v1/rerank',{'model':RERANK,'query':q['query'],'documents':[chunks[i]['search'] for i in candidates],'top_n':len(candidates)})
            ids=[x['index'] for x in response['results']]
            assert sorted(ids)==list(range(len(candidates)))
            reranked=[candidates[i] for i in ids]; t_rerank=1000*(time.perf_counter()-started)
            methods={}
            for name,rank,latency in [('bm25',a,t_bm),('dense',b,t_q+t_dense),('hybrid_rrf',hybrid,t_bm+t_q+t_dense+t_rrf),('hybrid_rerank',reranked,t_bm+t_q+t_dense+t_rrf+t_rerank)]:
                started=time.perf_counter(); parentkeys=list(dict.fromkeys(k for i in rank[:10] for k in chunks[i]['parent_keys'])); parenttext='\n'.join(parents[k]['text'] for k in parentkeys); t_expand=1000*(time.perf_counter()-started)
                methods[name]={'ranking':rank,'child_metrics':metrics(rank,chunks,q['gold']),'expanded_metrics':metrics(rank,expanded,q['gold']),
                               'latency_ms':latency,'expansion_ms':t_expand,'child_chars_at_10':sum(len(chunks[i]['primary']) for i in rank[:10]),
                               'expanded_chars_at_10':len(parenttext),'distinct_parents_at_10':len(parentkeys),'parent_keys_at_10':parentkeys,
                               'top10_children':[{'file':chunks[i]['file'],'block_ref':chunks[i].get('block_ref'),'text':chunks[i]['primary'],'heading':chunks[i]['heading']} for i in rank[:10]]}
            result['queries'].append({'id':q['id'],'query':q['query'],'gold':q['gold'],'rerank_response':response,'methods':methods})
            dump(output.with_suffix('.partial.json'),result)
            print(scenario,'block_parent',q['id'],{m:(v['child_metrics']['recall']['10'],v['expanded_metrics']['recall']['10']) for m,v in methods.items()},flush=True)
        dump(output,result); output.with_suffix('.partial.json').unlink()
    rows=[]; lines=['# Blocchi originali e contesto padre','','Metrica espansa: gold presente in uno dei padri dei primi k blocchi recuperati. Il costo include tutti i padri distinti; nessun riordino o filtro tramite gold. Non è un knowledge graph semantico.','',
        '| Corpus | Metodo | Recall@10 blocchi | Recall@10 con padri | Caratteri blocchi p50 | Caratteri con padri p50 | Ricerca p50 ms | Espansione p50 ms |',
        '|---|---|---:|---:|---:|---:|---:|---:|']
    for scenario in cases:
        r=json.loads((HERE/'results'/(scenario+'--block_parent.json')).read_text(encoding='utf-8'))
        for method in ['bm25','dense','hybrid_rrf','hybrid_rerank']:
            values=[q['methods'][method] for q in r['queries']]
            row={'scenario':scenario,'method':method,'child_recall10':statistics.mean(v['child_metrics']['recall']['10'] for v in values),'expanded_recall10':statistics.mean(v['expanded_metrics']['recall']['10'] for v in values),
                 'child_chars_p50':statistics.median(v['child_chars_at_10'] for v in values),'expanded_chars_p50':statistics.median(v['expanded_chars_at_10'] for v in values),
                 'latency_p50_ms':statistics.median(v['latency_ms'] for v in values),'expansion_p50_ms':statistics.median(v['expansion_ms'] for v in values)}
            rows.append(row); lines.append(f"| {scenario} | {method} | {row['child_recall10']:.1%} | {row['expanded_recall10']:.1%} | {row['child_chars_p50']:.0f} | {row['expanded_chars_p50']:.0f} | {row['latency_p50_ms']:.1f} | {row['expansion_p50_ms']:.2f} |")
    dump(HERE/'results/parent-summary.json',rows)
    (HERE/'results/parent-comparison.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')

if __name__=='__main__': main()
