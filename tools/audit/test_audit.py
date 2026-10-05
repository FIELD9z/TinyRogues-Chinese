"""Regression checks for completeness claims made by the audit tooling."""
import hashlib
import importlib.util
import json
import subprocess
import struct
import tempfile
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('audit_report',HERE/'report.py')
report=importlib.util.module_from_spec(spec);spec.loader.exec_module(report)
with tempfile.TemporaryDirectory(prefix='tinyrogues-audit-') as tmp:
    directory=Path(tmp)
    cases=[
        ('known','Nothing can stop me!','dialogue',True,'text'),
        ('unknown-context','Nothing can stop me!','dialogue',False,'text'),
        ('one-letter','A','',False,'text'),
        ('mixed-whitespace','  HP生命\r\n','',False,'text'),
        ('template','Count {number:n}','dialogue',False,'template-specification'),
        ('invalid-regex','(','',False,'regex-rule'),
        ('long','A'*12001,'',False,'text'),
    ]
    rows=[dict(id=key,source=text,context=context,contextKnown=known,route='fixture-input',kind=kind,
        origins=[dict(case=key)],flags=[],historicalNativeEvidence=[]) for key,text,context,known,kind in cases]
    input_path=directory/'inputs.jsonl'
    input_path.write_text(''.join(json.dumps(row,ensure_ascii=False)+'\n' for row in rows),encoding='utf-8')
    manifest=dict(inputRows=len(rows),sourceOccurrences=len(rows),uniqueSourceStrings=len({r['source'] for r in rows}),
        inputSha256=hashlib.sha256(input_path.read_bytes()).hexdigest(),issues=[])
    (directory/'input-manifest.json').write_text(json.dumps(manifest),encoding='utf-8')
    (directory/'legacy-lines.jsonl').write_text('',encoding='utf-8')
    subprocess.run(['dotnet','run','--project',str(HERE/'Audit.csproj'),'--',str(ROOT),str(directory)],check=True)
    checked={r['id']:r for r in report.lines(directory/'checked.jsonl')}
    assert len(checked)==len(cases)
    for row in rows: assert checked[row['id']]['source']==row['source']
    assert checked['known']['matched'] and checked['known']['status']=='static-match-only'
    assert checked['unknown-context']['matched'] and checked['unknown-context']['status']=='context-unresolved'
    assert checked['unknown-context']['discoveryMatched'] and not checked['unknown-context']['verifiedContextMatch']
    assert checked['one-letter']['remainingLatin']==['A']
    assert checked['mixed-whitespace']['remainingLatin']==['HP']
    assert checked['template']['status']=='template-requires-concrete-inputs'
    assert checked['invalid-regex']['status']=='invalid-regex'
    assert checked['long']['status']=='oversize-input-unverified'
    assert all(not r['currentNativeVerified'] and not r['meaningReviewed'] and not r['layoutVerified'] for r in checked.values())
    report.report(directory)
    with (directory/'checked.jsonl').open('a',encoding='utf-8') as stream: stream.write('\n')
    try: report.report(directory)
    except AssertionError as error: assert 'Stale or modified' in str(error)
    else: raise AssertionError('Modified results incorrectly accepted')
    extract_spec=importlib.util.spec_from_file_location('audit_extract',HERE/'extract_sources.py')
    extract=importlib.util.module_from_spec(extract_spec);extract_spec.loader.exec_module(extract)
    literals=['','A','  HP生命\r\n','B'*5000]
    encoded=[s.encode('utf-8') for s in literals]
    metadata=directory/'metadata.dat'
    table=b'';offset=0
    for value in encoded: table+=struct.pack('<II',len(value),offset);offset+=len(value)
    metadata.write_bytes(struct.pack('<IIIIII',0xFAB11BAF,31,24,len(table),24+len(table),offset)+table+b''.join(encoded))
    extracted=[];extraction_manifest={'inputs':{}}
    extract.metadata_scan(metadata,extracted,extraction_manifest)
    assert [row['text'] for row in extracted]==literals, 'Literal extraction filtered or normalized source'
    assert not extraction_manifest['inputs'][str(metadata)]['failures']
print('PASS: seven input classes retained; context claims, Latin detection, source preservation and stale-result rejection checked.')
