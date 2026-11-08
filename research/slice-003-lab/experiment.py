"""Retrieval-only experiment. Standard library; frozen gold, real HTTP, no product writes."""
import argparse
import collections
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import time
import unicodedata
import urllib.request
import urllib.error

import lab
from prepare import dump, sha

HERE=Path(__file__).resolve().parent
EMBED='bge-m3-Q5'
RERANK='bge-reranker-v2-m3'
KS=(1,5,10,20,50)

def norm(s): return ' '.join(unicodedata.normalize('NFKC',s).lower().replace('|',' ').split())

class Http:
    def __init__(self):
        self.base=os.environ['BPROJECTS_TEST_DGX_ENDPOINT'].rstrip('/').removesuffix('/v1')
        self.key=os.environ['BPROJECTS_TEST_DGX_API_KEY']
        self.calls=[]
        self.stage='preflight'

    def call(self,path,payload=None,base=None):
        started=time.perf_counter()
        encoded=json.dumps(payload,ensure_ascii=False).encode() if payload is not None else None
        req=urllib.request.Request((base or self.base)+path,encoded,{'Authorization':'Bearer '+self.key,'Content-Type':'application/json'})
        record={'stage':self.stage,'path':path,'model':payload.get('model') if payload else None,'input_sha256':sha(encoded) if encoded else None}
        try:
            with urllib.request.urlopen(req,timeout=180) as r: data=json.load(r)
            record.update(status='OK',response_sha256=sha(json.dumps(data,sort_keys=True).encode()),usage=data.get('usage') if isinstance(data,dict) else None)
            return data
        except Exception as e:
            record.update(status='ERROR',error=type(e).__name__,http_status=getattr(e,'code',None))
            raise
        finally:
            record['milliseconds']=1000*(time.perf_counter()-started)
            self.calls.append(record)
            with (HERE/'results/http-calls.jsonl').open('a',encoding='utf-8') as f: f.write(json.dumps(record)+'\n')

    def guard(self):
        props=self.call('/props')
        models=self.call('/v1/models')['data']
        resident={m['id'] for m in models if m.get('status',{}).get('value') in ('loaded','loading','sleeping')}
        if len(resident|{EMBED,RERANK}) > props.get('max_instances',0):
            raise RuntimeError('Router capacity insufficient without possible eviction; no model changes requested')
        return props,models

    def embed(self,texts,use_cache=True):
        cache=HERE/'cache/embeddings'
        cache.mkdir(parents=True,exist_ok=True)
        values={}
        keys=[sha((EMBED+'\0'+s).encode()) for s in texts]
        missing=[]
        for key,text in zip(keys,texts):
            p=cache/(key+'.json')
            if key in values: continue
            if use_cache and p.exists(): values[key]=json.loads(p.read_text())
            else: values[key]=None; missing.append((key,text))
        for start in range(0,len(missing),4):
            batch=missing[start:start+4]
            data=self.call('/v1/embeddings',{'model':EMBED,'input':[x[1] for x in batch]})
            rows=sorted(data['data'],key=lambda x:x['index'])
            if [r['index'] for r in rows] != list(range(len(batch))): raise ValueError('Embedding indices incomplete')
            for (key,_),row in zip(batch,rows):
                vec=row['embedding']
                if len(vec)!=1024 or not all(math.isfinite(x) for x in vec): raise ValueError('Invalid embedding')
                length=math.sqrt(sum(x*x for x in vec))
                if not length: raise ValueError('Zero embedding')
                values[key]=[x/length for x in vec]
                (cache/(key+'.json')).write_text(json.dumps(values[key]),encoding='utf-8')
            if start%100==0: print(f'embedding {start+len(batch)}/{len(missing)} unique inputs',flush=True)
        return [values[k] for k in keys],len(missing)

def read_scenario(scenario,variant):
    folder=HERE/'corpus'/scenario
    chunks=[]
    if variant.startswith('line'):
        # Use the original baseline chunker unchanged; exclude metadata files.
        rows,_=lab.read_chunks(folder)
        for c in rows:
            if c['file']=='hashes.txt': continue
            chunks.append(dict(c,primary=c['text'],source_type='Message' if c['file']=='pm-message.txt' else 'Document',
                               version_sha256=sha((folder/c['file']).read_bytes()),metadata_origin='lab.py heading heuristic',
                               search=c['text'] if variant=='line_text' else c['context']))
    else:
        for path in sorted(folder.iterdir()):
            if path.suffix not in ('.txt','.md','.docx') or path.name=='hashes.txt': continue
            if path.name=='pm-message.txt':
                text=path.read_text(encoding='utf-8').strip()
                chunks.append({'file':path.name,'primary':text,'text':text,'search':text,'heading':'','source_type':'Message','version_sha256':sha(path.read_bytes()),'ranges':[]})
                continue
            parsed=json.loads((HERE/'parsed'/scenario/(path.name+'.json')).read_text(encoding='utf-8'))
            if parsed['status']!='Complete': raise ValueError('Incomplete parser result')
            blocks={b['ref']:b for b in parsed['blocks']}
            for c in parsed['chunks']:
                primary=[]
                refs=[]
                for r in c['ranges']:
                    b=blocks[r['blockRef']]
                    assert sha(b['text'].encode())==r['sourceTextSha256']
                    assert 0<=r['start']<=r['end']<=len(b['text'])
                    if r['ownership']=='Primary': primary.append(b['text'][r['start']:r['end']])
                    refs.append(dict(r,locator=b.get('locator'),headingPath=b.get('headingPath')))
                text='\n'.join(primary)
                chunks.append({'file':path.name,'primary':text,'text':c['text'],'search':text if variant=='docling_text' else c['contextualizedText'],
                               'heading':c.get('headingPath',[]),'source_type':'Document','version_sha256':sha(path.read_bytes()),'ranges':refs,'chunkKey':c['chunkKey'], 'metadata_origin':'existing parser'})
    for i,c in enumerate(chunks): c['id']=i
    return chunks

class Bm25:
    """Same scoring/tokenizer as lab.py; index construction separated from query timing."""
    def __init__(self,chunks):
        self.freq=[collections.Counter(lab.words(c['search'])) for c in chunks]
        self.length=[sum(d.values()) for d in self.freq]
        self.avg=sum(self.length)/max(1,len(chunks))
        self.df=collections.Counter(w for d in self.freq for w in d)
    def rank(self,query):
        n=len(self.freq)
        scores=[sum(math.log(1+(n-self.df[w]+.5)/(self.df[w]+.5))*d[w]*2.2/(d[w]+1.2*(.25+.75*self.length[i]/max(1,self.avg))) for w in set(lab.words(query)) if d[w]) for i,d in enumerate(self.freq)]
        return sorted(range(n),key=lambda i:(-scores[i],i)),scores

def relevant(c,g): return c['file']==g['file'] and norm(g['contains']) in norm(c['primary'])

def metrics(rank,chunks,gold):
    positions={g['id']:next((i+1 for i,c in enumerate(rank) if relevant(chunks[c],g)),None) for g in gold}
    return {'gold_ranks':positions,'first_correct_rank':min((p for p in positions.values() if p),default=None),
            'hit':{str(k):int(any(p and p<=k for p in positions.values())) for k in KS},
            'recall':{str(k):sum(bool(p and p<=k) for p in positions.values())/len(gold) for k in KS},
            'missing_at_10':[g for g,p in positions.items() if p is None or p>10],
            'not_in_candidate_set':[g for g,p in positions.items() if p is None]}

def run(scenario,variant,http):
    out=HERE/'results'/(scenario+'--'+variant+'.json')
    if out.exists(): print('retaining completed result',out.name,flush=True); return
    cases=json.loads((HERE/'retrieval-cases.json').read_text(encoding='utf-8'))['scenarios'][scenario]
    chunks=read_scenario(scenario,variant)
    indexability={g['id']:any(relevant(c,g) for c in chunks) for case in cases for g in case['gold']}
    dump(HERE/'indexes'/(scenario+'--'+variant+'.json'),chunks)
    started=time.perf_counter(); bm=Bm25(chunks); bm_build=1000*(time.perf_counter()-started)
    http.guard()
    http.stage=f'{scenario}/{variant}/corpus'
    before=len(http.calls); started=time.perf_counter()
    vecs,new=http.embed([c['search'] for c in chunks])
    build={'wall_ms':1000*(time.perf_counter()-started),'new_unique_inputs':new,'http_calls':len(http.calls)-before,'http_ms':sum(x['milliseconds'] for x in http.calls[before:]),'tokens':sum((x.get('usage') or {}).get('prompt_tokens',0) for x in http.calls[before:])}
    result={'scenario':scenario,'variant':variant,'embed_model':EMBED,'rerank_model':RERANK,'chunks':len(chunks),'corpus_sha256':sha(json.dumps(chunks,sort_keys=True).encode()),'case_file_sha256':sha((HERE/'retrieval-cases.json').read_bytes()),'indexable_gold':indexability,'bm25_build_ms':bm_build,'embedding_build':build,'candidate_k':50,'rrf_k':60,'queries':[]}
    for case in cases:
        http.stage=f'{scenario}/{variant}/{case["id"]}'
        started=time.perf_counter(); a,sa=bm.rank(case['query']); bm_ms=1000*(time.perf_counter()-started)
        before=len(http.calls); started=time.perf_counter(); qvec,qnew=http.embed([case['query']],use_cache=False); qe_ms=1000*(time.perf_counter()-started)
        qe_http=sum(x['milliseconds'] for x in http.calls[before:])
        started=time.perf_counter(); b,sb=lab.cos_rank(qvec[0],vecs); dense_ms=1000*(time.perf_counter()-started)
        started=time.perf_counter(); hybrid,sh=lab.rrf(a,b,len(chunks)); fusion_ms=1000*(time.perf_counter()-started)
        candidates=hybrid[:50]
        started=time.perf_counter()
        try:
            response=http.call('/v1/rerank',{'model':RERANK,'query':case['query'],'documents':[chunks[i]['search'] for i in candidates],'top_n':len(candidates)})
            ids=[r['index'] for r in response['results']]
            if sorted(ids)!=list(range(len(candidates))): raise ValueError('Reranker indices incomplete')
            reranked=[candidates[i] for i in ids]
            sr={candidates[r['index']]:r['relevance_score'] for r in response['results']}
            rr_error=None
        except Exception as e:
            response=None; reranked=[]; sr={}; rr_error={'error':type(e).__name__,'http_status':getattr(e,'code',None)}
        rerank_ms=1000*(time.perf_counter()-started)
        methods={}
        for name,rank,scores,ms in [('bm25',a,sa,bm_ms),('dense',b,sb,qe_ms+dense_ms),('hybrid_rrf',hybrid,sh,bm_ms+qe_ms+dense_ms+fusion_ms),('hybrid_rerank',reranked,sr,bm_ms+qe_ms+dense_ms+fusion_ms+rerank_ms)]:
            methods[name]={'status':'ERROR' if name=='hybrid_rerank' and rr_error else 'MEASURED',**metrics(rank,chunks,case['gold']),'latency_ms':ms,'ranking':rank,'scores':[scores[i] for i in rank], 'top10':[{'id':i,'file':chunks[i]['file'],'heading':chunks[i]['heading'],'source_type':chunks[i]['source_type'],'version_sha256':chunks[i]['version_sha256'],'text':chunks[i]['primary']} for i in rank[:10]]}
        result['queries'].append({'id':case['id'],'query':case['query'],'gold':case['gold'],'query_embedding_ms':qe_ms,'query_embedding_http_ms':qe_http,'query_embedding_cache_hit':qnew==0,'dense_search_ms':dense_ms,'bm25_search_ms':bm_ms,'fusion_ms':fusion_ms,'reranking_ms':rerank_ms,'rerank_response':response,'rerank_error':rr_error,'methods':methods})
        dump(out.with_suffix('.partial.json'),result)
        print(scenario,variant,case['id'],{m:methods[m]['recall']['10'] for m in methods},flush=True)
    dump(out,result)
    out.with_suffix('.partial.json').unlink()

def nli(http):
    base=os.environ['BPROJECTS_LAB_NLI_ENDPOINT'].rsplit('/v1/nli',1)[0]
    cases=json.loads((HERE/'nli-cases.json').read_text(encoding='utf-8'))['cases']
    # Additional pairs are original evidence from the frozen retrieval inputs.
    frozen=json.loads((HERE/'retrieval-cases.json').read_text(encoding='utf-8'))['scenarios']
    allgold={g['id']:g for case in frozen['T083-multi'] for g in case['gold']}
    retrieved=json.loads((HERE/'results/T083-multi--docling_context.json').read_text(encoding='utf-8'))
    def found(id):
        return any(row['methods']['hybrid_rerank']['gold_ranks'].get(id) in range(1,11) for row in retrieved['queries'])
    additions=[('retrieved-warranty','M04','M13','contradiction'),('retrieved-equivalence','M05','M09','entailment'),('retrieved-neutral','M01','M15','neutral')]
    for id,a,b,expected in additions:
        cases.append({'id':id,'premise':allgold[a]['contains'],'hypothesis':allgold[b]['contains'],'expected':expected,'gold_ids':[a,b],'both_retrieved_top10':found(a) and found(b)})
    cases += [
        {'id':'retrieved-condition-preserved','premise':allgold['M17']['contains'],'hypothesis':'Se il collaudo non viene superato, il pagamento della seconda rata non viene effettuato.','expected':'entailment','gold_ids':['M17'],'both_retrieved_top10':found('M17')},
        {'id':'retrieved-condition-dropped','premise':allgold['M17']['contains'],'hypothesis':'Il pagamento della seconda rata è sospeso.','expected':'neutral','gold_ids':['M17'],'both_retrieved_top10':found('M17')},
        {'id':'retrieved-exception-dropped','premise':allgold['M12']['contains'],'hypothesis':'Nessun ordine urgente è accettato dopo le 16:00.','expected':'contradiction','gold_ids':['M12'],'both_retrieved_top10':found('M12')}]
    single_gold={g['id']:g for case in frozen['T082-single'] for g in case['gold']}
    single_result=json.loads((HERE/'results/T082-single--docling_context.json').read_text(encoding='utf-8'))
    single_query=next(q for q in single_result['queries'] if q['id']=='S05')
    cases.append({'id':'retrieved-numerical-compatible','premise':single_gold['S05']['contains'],
                  'hypothesis':'Le segnalazioni di guasto con priorità alta sono prese in carico entro 60 minuti, esclusi i giorni festivi.',
                  'expected':'entailment','gold_ids':['S05'],'both_retrieved_top10':single_query['methods']['hybrid_rerank']['gold_ranks']['S05'] in range(1,11)})
    # Freeze these expectations BEFORE the first NLI request.
    dump(HERE/'nli-extended-cases.json',{'cases':cases,'note':'Diagnostic author judgments, not acceptance criteria. Retrieved pairs use original gold evidence after verified retrieval.'})
    rows=[]
    for case in cases:
        started=time.perf_counter()
        try:
            response=http.call('/v1/nli',{'premise':case['premise'],'hypothesis':case['hypothesis']},base=base)
            rows.append(dict(case,response=response,milliseconds=1000*(time.perf_counter()-started)))
        except Exception as e: rows.append(dict(case,error=type(e).__name__,http_status=getattr(e,'code',None)))
        dump(HERE/'results/nli.json',rows)
    print('NLI completed',len(rows),flush=True)

def main():
    ap=argparse.ArgumentParser(); ap.add_argument('--scenario',default='all'); ap.add_argument('--variant',default='all'); ap.add_argument('--nli',action='store_true'); args=ap.parse_args()
    (HERE/'results').mkdir(exist_ok=True)
    http=Http()
    if args.nli: nli(http); return
    props,models=http.guard()
    configurations=[]
    for model in models:
        if model['id'] not in (EMBED,RERANK): continue
        argv=model['status']['args']; config={}
        for i,arg in enumerate(argv):
            if arg in ('--model','--ctx-size','--batch-size','--ubatch-size','--parallel','--pooling','--cache-type-k','--cache-type-v','--flash-attn'):
                config[arg]=argv[i+1].split('/')[-1] if arg=='--model' else argv[i+1]
        configurations.append({'id':model['id'],'configuration':config,'artifact_sha256':'not exposed by HTTP; unverified'})
    dump(HERE/'results/models.json',{'router_build':props.get('build_info'),'models':configurations,'revision_limit':'GGUF filename and quantization observed, exact weight revision unavailable via API','python':os.sys.version.split()[0]})
    scenarios=['N4-fixtures','T082-single','T083-multi'] if args.scenario=='all' else [args.scenario]
    variants=['line_text','line_context','docling_text','docling_context'] if args.variant=='all' else [args.variant]
    for scenario in scenarios:
        for variant in variants:
            try: run(scenario,variant,http)
            except Exception as e:
                error={'scenario':scenario,'variant':variant,'error':type(e).__name__,'detail':str(e).replace(http.key,'[REDACTED]').replace(http.base,'[DGX]')}
                dump(HERE/'results'/(scenario+'--'+variant+'--error.json'),error)
                print(error,flush=True)

if __name__=='__main__': main()
