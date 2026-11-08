"""Audit recorded artifacts and original references; not a product test or acceptance gate."""
import collections
import hashlib
import json
from pathlib import Path
import subprocess
from prepare import dump,sha
from experiment import metrics,relevant
import lab

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[1]

def main():
    checks=[]
    for name in ['lab.py','cases.json','nli-cases.json','research-preliminare.md']:
        path='research/slice-003-lab/'+name
        before=subprocess.check_output(['git','show','a312c0651930440ff6164cf765d2f15583f3bf1c:'+path],cwd=ROOT)
        actual=(HERE/name).read_bytes()
        assert before.replace(b'\r\n',b'\n')==actual.replace(b'\r\n',b'\n'),name
        checks.append({'check':'baseline unchanged','file':name,'sha256':sha(actual),'status':'PASS'})
    casefile=HERE/'retrieval-cases.json'
    cases=json.loads(casefile.read_text(encoding='utf-8'))['scenarios']
    hashes=json.loads((HERE/'input-hashes.json').read_text(encoding='utf-8'))
    for name,digest in hashes.items():
        path=ROOT/'backend/tests/bProjects.IntegrationTests/LargeDocumentCorpus.cs' if name=='generator' else HERE/name
        data=path.read_bytes()
        if name=='generator': data=data.replace(b'\r\n',b'\n')
        assert sha(data)==digest,name
    checks.append({'check':'frozen input SHA256','files':len(hashes),'status':'PASS'})
    for scenario in ['T082-single','T083-multi']:
        gold=json.loads((HERE/'corpus'/scenario/'gold.json').read_text(encoding='utf-8'))
        original={g['Id']:g['Sentence'] for g in gold['Gold']}
        for q in cases[scenario]:
            for g in q['gold']: assert g['contains']==original[g['id']],g['id']
        checks.append({'check':'literal original gold','scenario':scenario,'count':len(original),'status':'PASS'})
    configurations=0; queries=0; method_results=0; indexability={}
    for scenario,qs in cases.items():
        for variant in ['line_text','line_context','docling_text','docling_context']:
            path=HERE/'results'/(scenario+'--'+variant+'.json')
            r=json.loads(path.read_text(encoding='utf-8'))
            chunks=json.loads((HERE/'indexes'/path.name).read_text(encoding='utf-8'))
            assert r['case_file_sha256']==sha(casefile.read_bytes())
            assert r['corpus_sha256']==sha(json.dumps(chunks,sort_keys=True).encode())
            assert len(r['queries'])==len(qs)
            indexability[scenario+'/'+variant]=r['indexable_gold']
            configurations+=1
            for q,original in zip(r['queries'],qs):
                assert q['id']==original['id'] and q['gold']==original['gold'] and q['query']==original['query']
                queries+=1
                assert not q['query_embedding_cache_hit']
                for method,row in q['methods'].items():
                    method_results+=1
                    rank=row['ranking']
                    assert len(rank)==len(set(rank)) and all(0<=i<len(chunks) for i in rank)
                    if row['status']=='MEASURED': assert len(rank)==(min(50,len(chunks)) if method=='hybrid_rerank' else len(chunks))
                    computed=metrics(rank,chunks,q['gold'])
                    for key,value in computed.items(): assert row[key]==value,(scenario,variant,q['id'],method,key)
                if q['methods']['hybrid_rerank']['status']=='MEASURED':
                    assert set(q['methods']['hybrid_rerank']['ranking'])==set(q['methods']['hybrid_rrf']['ranking'][:50])
    checks.append({'check':'ranks, metrics, candidate preservation, equal query/gold across methods','configurations':configurations,'queries':queries,'method_results':method_results,'status':'PASS'})
    for scenario in cases:
        children=json.loads((HERE/'indexes'/(scenario+'--line_text.json')).read_text(encoding='utf-8'))
        texts={}; cursors={}; locators=[]
        for child in children:
            file=child['file']; source=HERE/'corpus'/scenario/file
            if file not in texts:
                texts[file]=lab.decode_docx(source) if source.suffix=='.docx' else source.read_text(encoding='utf-8-sig')
                cursors[file]=0
            raw=texts[file]; start=raw.find(child['primary'],cursors[file]); assert start>=0,(file,child['id'])
            end=start+len(child['primary']); cursors[file]=end
            locators.append({'chunk_id':child['id'],'file':file,'version_sha256':child['version_sha256'],'start_codepoint':start,'end_codepoint':end,
                             'start_utf16':len(raw[:start].encode('utf-16-le'))//2,'end_utf16':len(raw[:end].encode('utf-16-le'))//2,
                             'line_or_paragraph':raw[:start].count('\n')+1,'offset_domain':'DOCX decoded paragraph text' if source.suffix=='.docx' else 'decoded original text',
                             'quote_sha256':sha(raw[start:end].encode())})
        dump(HERE/'indexes'/(scenario+'--line-locators.json'),locators)
    checks.append({'check':'baseline line quotes mapped sequentially to exact original text/decoded DOCX paragraphs','status':'PASS'})
    dump(HERE/'results/integrity-audit.json',{'checks':checks,'indexability':indexability,'note':'Artifact integrity only. Does not assert retrieval quality, semantic correctness or product acceptance.'})
    # Recover actual cold embedding work from request hashes, including the interrupted UTF-8 preparation run.
    calls=[json.loads(x) for x in (HERE/'results/http-calls.jsonl').read_text().splitlines()]
    byhash=collections.defaultdict(list)
    for call in calls:
        if call['path']=='/v1/embeddings' and call['status']=='OK': byhash[call['input_sha256']].append(call)
    cold=[]
    for path in sorted((HERE/'indexes').glob('*.json')):
        if path.name.endswith(('--block_parent.json','--line-locators.json')): continue
        chunks=json.loads(path.read_text(encoding='utf-8'))
        unique=list(dict.fromkeys(c['search'] for c in chunks))
        matched=[]; missing=[]
        for i in range(0,len(unique),4):
            payload={'model':'bge-m3-Q5','input':unique[i:i+4]}
            digest=sha(json.dumps(payload,ensure_ascii=False).encode())
            if byhash[digest]: matched.append(byhash[digest][0])
            else: missing.append(i)
        cold.append({'index':path.stem,'unique_inputs':len(unique),'matched_batch_calls':len(matched),'unmatched_batches':missing,
                     'http_ms':sum(x['milliseconds'] for x in matched),'prompt_tokens':sum((x.get('usage') or {}).get('prompt_tokens',0) for x in matched),
                     'complete':not missing,'note':'First successful exact request hash match for canonical batches of four. Incomplete matching is NOT a full cold-build timing. Shared requests may appear in more than one index; do not sum across indexes.'})
    dump(HERE/'results/embedding-cold-work.json',cold)
    totals=[]
    for model in sorted({c.get('model') or c['path'] for c in calls}):
        rows=[c for c in calls if (c.get('model') or c['path'])==model]
        totals.append({'model_or_endpoint':model,'calls':len(rows),'errors':sum(c['status']!='OK' for c in rows),'http_seconds':sum(c['milliseconds'] for c in rows)/1000,'prompt_tokens':sum((c.get('usage') or {}).get('prompt_tokens',0) for c in rows)})
    dump(HERE/'results/http-totals.json',{'totals':totals,'note':'Includes pilot and aborted preparation; not just final campaign. NLI token counts are not exposed (0 here means unavailable). Initial manual smoke probes are excluded.'})
    print('Artifact audit PASS:',configurations,'configurations,',queries,'queries,',method_results,'method results')

if __name__=='__main__': main()
