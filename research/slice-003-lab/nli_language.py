"""User-authorized paired IT/EN diagnostic; expectations are never fitted to scores."""
import json
import os
import time
from experiment import Http,HERE
from prepare import dump

TRANSLATIONS={
 'exact-conflict':('The contractual payment term is exactly 45 days.','The contractual payment term is exactly 30 days.'),
 'language-paraphrase':('The invoice must be paid within 30 days of its issue date.','Payment of the invoice cannot take place later than 30 days after it is issued.'),
 'different-scopes':('For Italian customers, payment is due within 30 days.','For foreign customers, payment is due within 45 days.'),
 'numerical-entailment':('Payment must be made within 30 days.','Payment must be made within 60 days.'),
 'reverse-non-entailment':('Payment must be made within 45 days.','Payment must be made within 30 days.'),
 'retrieved-warranty':('The warranty on spare parts lasts 24 months from delivery.','The warranty on spare parts lasts 36 months from delivery.'),
 'retrieved-equivalence':('Spare parts requested through the standard procedure are delivered within 5 working days.','Standard spare parts arrive no later than within one working week.'),
 'retrieved-neutral':('The Zefiro framework contract lasts 48 months from the date of signing.','Final acceptance testing will take place on 12 March 2027 at the Brescia site.'),
 'retrieved-condition-preserved':('If acceptance testing is not passed, payment of the second instalment is suspended.','If acceptance testing is not passed, payment of the second instalment is not made.'),
 'retrieved-condition-dropped':('If acceptance testing is not passed, payment of the second instalment is suspended.','Payment of the second instalment is suspended.'),
 'retrieved-exception-dropped':('Urgent orders are not accepted after 16:00, except those from Gold-category customers.','No urgent order is accepted after 16:00.'),
 'retrieved-numerical-compatible':('High-priority fault reports are taken in hand within 40 minutes, excluding public holidays.','High-priority fault reports are taken in hand within 60 minutes, excluding public holidays.')
}

def main():
    source=HERE/'results/nli.json'
    if not source.exists(): source=HERE/'results/nli-baseline.json'
    italian=json.loads(source.read_text(encoding='utf-8'))
    cases=[{'id':c['id'],'premise':TRANSLATIONS[c['id']][0],'hypothesis':TRANSLATIONS[c['id']][1],'expected':c['expected']} for c in italian]
    dump(HERE/'nli-english-cases.json',{'cases':cases,'translation':'Authored by the research agent; not independently reviewed. Same labels and case IDs as Italian; not a general language benchmark.'})
    http=Http(); http.stage='paired-language-en'
    base=os.environ['BPROJECTS_LAB_NLI_ENDPOINT'].rsplit('/v1/nli',1)[0]
    rows=[]
    for c,it in zip(cases,italian):
        started=time.perf_counter()
        response=http.call('/v1/nli',{'premise':c['premise'],'hypothesis':c['hypothesis']},base=base)
        rows.append(dict(c,response=response,italian_response=it.get('response'),milliseconds=1000*(time.perf_counter()-started)))
        dump(HERE/'results/nli-language.json',rows)
        print(c['id'],c['expected'],'IT',it.get('response',{}).get('prediction'),'EN',response.get('prediction'),flush=True)

if __name__=='__main__': main()
