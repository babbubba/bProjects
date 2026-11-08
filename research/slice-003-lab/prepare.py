"""Materialize only synthetic inputs; call the existing parser, never modify it."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import urllib.request
import uuid

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]

def sha(data): return hashlib.sha256(data).hexdigest()
def dump(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--single-original', type=Path, required=True)
    ap.add_argument('--parser-container', required=True)
    args = ap.parse_args()
    single = HERE/'corpus/T082-single'
    original = args.single_original.read_bytes()
    assert sha(original) == 'a926fb1da97b8b01edfa61e67e87967a8249f6bd37e22d239fd642eb491530f6'
    (single/'capitolato-aurora.txt').write_bytes(original)
    gold = json.loads((single/'gold.json').read_text(encoding='utf-8'))
    gold['FillerChars'] = None
    gold['Source'] = 'Exact synthetic original from T082 run4; original SHA verified; gold unchanged from LargeDocumentCorpus.cs'
    gold['Documents'][0]['Sha256'] = sha(original)
    gold['Documents'][0]['PlainTextSha256'] = sha(original)
    dump(single/'gold.json', gold)
    (single/'hashes.txt').write_text('capitolato-aurora.txt sha256='+sha(original)+'\n', encoding='utf-8')
    fixture = HERE/'corpus/N4-fixtures'
    fixture.mkdir(exist_ok=True)
    for name in ['txt-conflict-a.txt','txt-conflict-b.txt','txt-negations.txt','md-basic.md']:
        shutil.copyfile(ROOT/'tests/fixtures/documents'/name, fixture/name)
    pm = "Il cliente mi ha confermato per telefono che il termine di pagamento e' di 45 giorni."
    (fixture/'pm-message.txt').write_text(pm+'\n', encoding='utf-8')
    multi = json.loads((HERE/'corpus/T083-multi/gold.json').read_text(encoding='utf-8'))
    (HERE/'corpus/T083-multi/pm-message.txt').write_text(multi['PmStatement']+'\n', encoding='utf-8')
    # The existing cases are retained verbatim, including wording and gold strings.
    cases = json.loads((HERE/'cases.json').read_text(encoding='utf-8'))['cases']
    scenarios = {'N4-fixtures': [{'id':c['id'],'query':c['query'],'gold':[dict(c['gold'],id=c['id'])]} for c in cases]}
    single_queries = [
        'Per quanto tempo si conservano i documenti di trasporto?',
        'Chi può accedere al portale Aurora?',
        'Quando si emette la fattura elettronica nei mesi ordinari?',
        'Quali eccezioni stagionali si applicano alla scadenza di fatturazione?',
        'Quali tempi e quali esclusioni valgono per la presa in carico dei guasti urgenti?',
        'Quali autorizzazioni servono per acquistare con procedura urgente?',
        'Quali resi rifiuta il magazzino di Verona?',
        'Quando si effettuano i backup e per quanto tempo vengono mantenuti?',
        'Come si calcola la penale per consegne tardive e qual è il suo tetto?',
        'In quali condizioni il fornitore è esonerato per uno sciopero dei trasportatori?',
        'Quale sede dispone di assistenza durante la notte?',
        'Quale canale deve essere usato per le comunicazioni contrattuali?',
        'Quale termine di pagamento decorre dal ricevimento delle fatture?',
        'Quale termine di pagamento decorre dalla data fattura?',
        'Qual è la finestra di manutenzione prevista al sabato?',
        'Qual è la finestra di manutenzione prevista alla domenica?']
    multi_queries = {
        'M01':'Quanto dura il contratto quadro Zefiro?', 'M02':'Il contratto si rinnova automaticamente?',
        'M03':'Quale termine di pagamento prevede il contratto quadro?',
        'M04':'Quanto dura la garanzia nel contratto quadro?',
        'M05':'Quanto si attende per ricevere i ricambi ordinari secondo il contratto quadro?',
        'M06':'Dove sono definite le penali previste dal contratto quadro?',
        'M07':'Che cosa comprende il pacchetto Ambra?',
        'M08':'Quanti ordini al giorno sono consentiti per sede secondo l’allegato tecnico?',
        'M09':'Entro quando arrivano i ricambi secondo l’allegato tecnico?',
        'M10':'Con quale frequenza viene aggiornato il monitoraggio nel pacchetto Ambra?',
        'M11':'Come si calcolano le penali di ritardo dell’Allegato C?',
        'M12':'Quali eccezioni all’orario limite esistono per gli ordini urgenti?',
        'M13':'Quanto dura la garanzia secondo il verbale di collaudo?',
        'M14':'Quale termine di pagamento prevede il verbale di collaudo?',
        'M15':'Quando e dove si svolgerà il collaudo finale?',
        'M16':'Si possono modificare i listini durante il collaudo?',
        'M17':'Che cosa succede alla seconda rata se il collaudo fallisce?',
        'P1':'Quale limite giornaliero di ordini ha comunicato il PM?',
        'P2':'Quale termine di pagamento ha comunicato il PM?'}
    for scenario in ['T082-single','T083-multi']:
        g = json.loads((HERE/'corpus'/scenario/'gold.json').read_text(encoding='utf-8'))
        names = {d['Key']:d['FileName'] for d in g['Documents']} | {'pm':'pm-message.txt'}
        evidence = {f['Id']:{'id':f['Id'],'file':names[f['DocKey']], 'contains':f['Sentence'], 'zone':f['Zone'], 'classification':f['Class']} for f in g['Gold']}
        scenarios[scenario] = [{'id':f['Id'],'query':single_queries[i] if scenario=='T082-single' else multi_queries[f['Id']], 'gold':[evidence[f['Id']]]} for i,f in enumerate(g['Gold'])]
        packs = ([('all-payments','Quali termini di pagamento sono presenti nel capitolato?',['S13','S14']),
                  ('all-maintenance','Quali finestre di manutenzione sono indicate?',['S15','S16']),
                  ('invoice-exceptions','Quando si emette la fattura, incluse le eccezioni?',['S03','S04'])]
                 if scenario=='T082-single' else
                 [('all-payments','Quali termini di pagamento risultano dai documenti e dal PM?',['M03','M14','P2']),
                  ('all-warranty','Quali durate della garanzia risultano dai documenti?',['M04','M13']),
                  ('all-limits','Quali limiti giornalieri di ordini per sede risultano da documenti e PM?',['M08','P1']),
                  ('equivalent-delivery','Quali tempi di consegna sono previsti per i ricambi ordinari?',['M05','M09']),
                  ('penalty-reference','Dove sono definite e come si calcolano le penali di consegna?',['M06','M11']),
                  ('ambra-complements','Quali funzioni comprende Ambra e con quale frequenza sono aggiornate?',['M07','M10'])])
        scenarios[scenario] += [{'id':i,'query':q,'gold':[evidence[x] for x in ids]} for i,q,ids in packs]
    dump(HERE/'retrieval-cases.json', {'version':'real-fixtures-1','note':'Original gold sentences unchanged. Research queries authored before inference; binary evidence relevance, no graded nDCG judgments.', 'scenarios':scenarios})
    endpoint = os.environ['BPROJECTS_TEST_PARSER_ENDPOINT'].rstrip('/')
    with urllib.request.urlopen(endpoint+'/version') as r: version = json.load(r)
    secret = os.environ.get('BPROJECTS_TEST_PARSER_SECRET')
    if not secret:
        env = json.loads(subprocess.check_output(['docker','inspect',args.parser_container,'--format','{{json .Config.Env}}']))
        secret = next(x.split('=',1)[1] for x in env if x.startswith('PARSER_INTERNAL_SECRET='))
    dump(HERE/'results/parser-version.json',version)
    ledger=[]
    for scenario in scenarios:
        for path in sorted((HERE/'corpus'/scenario).iterdir()):
            if path.suffix not in ['.txt','.md','.docx'] or path.name in ['hashes.txt','pm-message.txt']: continue
            output = HERE/'parsed'/scenario/(path.name+'.json')
            raw=path.read_bytes()
            if output.exists():
                saved=json.loads(output.read_text(encoding='utf-8'))
                if saved['originalSha256']==sha(raw) and saved['configHash']==version['configHash']: continue
            fields={'declaredFormat':path.suffix[1:],'documentVersionId':str(uuid.uuid5(uuid.NAMESPACE_URL,sha(raw))), 'originalSha256':sha(raw),'configHash':version['configHash'], 'maxExpandedBytes':250*1024*1024,'maxPages':2000,'maxBlocks':500000,'maxOutputBytes':250*1024*1024,'timeoutSeconds':900}
            boundary='lab'+uuid.uuid4().hex
            body=b''
            for k,v in fields.items(): body+=f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n'.encode()
            body+=f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{path.name}"\r\nContent-Type: application/octet-stream\r\n\r\n'.encode()+raw+f'\r\n--{boundary}--\r\n'.encode()
            request=urllib.request.Request(endpoint+'/convert',body,{'Content-Type':'multipart/form-data; boundary='+boundary,'x-internal-secret':secret})
            started=time.perf_counter()
            try:
                with urllib.request.urlopen(request,timeout=920) as r: result=json.load(r)
                assert result['originalSha256']==sha(raw)
                dump(output,result)
                row={'scenario':scenario,'file':path.name,'sha256':sha(raw),'status':result['status'],'blocks':len(result['blocks']),'chunks':len(result['chunks']),'seconds':time.perf_counter()-started}
            except Exception as e: row={'scenario':scenario,'file':path.name,'error':type(e).__name__,'http_status':getattr(e,'code',None),'seconds':time.perf_counter()-started}
            ledger.append(row)
            dump(HERE/'results/parsing.json',ledger)
            print(json.dumps(row),flush=True)
    dump(HERE/'input-hashes.json',{str(p.relative_to(HERE)).replace('\\','/'):sha(p.read_bytes()) for base in ['corpus','parsed'] for p in sorted((HERE/base).rglob('*')) if p.is_file()} | {'retrieval-cases.json':sha((HERE/'retrieval-cases.json').read_bytes()),'generator':sha((ROOT/'backend/tests/bProjects.IntegrationTests/LargeDocumentCorpus.cs').read_bytes().replace(b'\r\n',b'\n'))})

if __name__=='__main__': main()
