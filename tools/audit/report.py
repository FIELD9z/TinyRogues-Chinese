"""Verify row conservation and write complete local review queues and a readable report."""
import argparse
import collections
import hashlib
import json
from pathlib import Path

LABELS={
    'context-unresolved':'入口未确认（匹配结果仅作线索）',
    'catalog-no-match':'指定入口未匹配',
    'matched-with-latin-needs-review':'已匹配但输出仍有拉丁字母',
    'static-match-only':'静态匹配通过，原生、译义和排版待验证',
    'unchanged-match-needs-review':'匹配但文本未变，待确认保留原因',
    'regex-requires-concrete-inputs':'旧正则需继续核对具体变量组合',
    'template-requires-concrete-inputs':'词库模板需具体游戏输入',
    'oversize-input-unverified':'长文本保留，需确认用途与入口',
    'preservation-needs-review':'数值或图标保留需复核',
    'check-error':'检查执行异常', 'invalid-regex':'旧正则语法异常'
}


def lines(path):
    with path.open(encoding='utf-8-sig') as stream:
        for line in stream: yield json.loads(line)


def report(directory):
    manifest=json.loads((directory/'input-manifest.json').read_text(encoding='utf-8'))
    summary=json.loads((directory/'check-summary.json').read_text(encoding='utf-8'))
    for filename,expected in [('inputs.jsonl',summary['inputSha256']),('checked.jsonl',summary['checkedSha256'])]:
        digest=hashlib.sha256()
        with (directory/filename).open('rb') as stream:
            for chunk in iter(lambda:stream.read(1024*1024),b''): digest.update(chunk)
        assert digest.hexdigest().lower()==expected.lower(), 'Stale or modified audit data: '+filename
    assert manifest['inputSha256'].lower()==summary['inputSha256'].lower(), 'Input manifest does not match completed check'
    original={r['id']:r for r in lines(directory/'inputs.jsonl')}
    matched_contexts=collections.defaultdict(set)
    for row in lines(directory/'checked.jsonl'):
        if row['verifiedContextMatch']: matched_contexts[row['source']].add(row['context'])
    seen=set(); statuses=collections.Counter(); routes=collections.Counter(); queue_counts=collections.Counter()
    examples=[]; dialogue_no_match={}; native_sources=set(); latin_sources=set()
    queue_names=['all-review-items','dialogue-unmatched','english-residue','legacy-rules','disabled-translations','preservation-review','captured-unmatched-candidates']
    handles={name:(directory/(name+'.jsonl')).open('w',encoding='utf-8') for name in queue_names}
    def emit(name,row):
        handles[name].write(json.dumps(row,ensure_ascii=False,separators=(',',':'))+'\n'); queue_counts[name]+=1
    for row in lines(directory/'checked.jsonl'):
        assert row['id'] in original and row['id'] not in seen, 'Missing or duplicated input ID'
        old=original[row['id']]
        assert all(row[key]==value for key,value in old.items()), 'Source, context or provenance changed'
        assert row['contextKnown'] or not row['verifiedContextMatch'], 'Unknown context promoted to context match'
        assert row['contextKnown'] or row['status']!='static-match-only', 'Unknown context promoted to pass'
        assert not row['currentNativeVerified'] and not row['layoutVerified'] and not row['meaningReviewed'], 'Static checker promoted unverified dimensions'
        seen.add(row['id']);statuses[row['status']]+=1;routes[row['route']]+=1
        if row['sourceHasLatin']: latin_sources.add(row['source'])
        if row['historicalNativeEvidence']: native_sources.add(row['source'])
        row['reviewReason']=LABELS.get(row['status'],row['reason'])
        row['nextAction']='确认显示用途和调用入口；记录原生样本；审校译义与排版'
        row['relatedMatchedContexts']=sorted(matched_contexts[row['source']]-{row['context']})
        if not row['matched'] and row['relatedMatchedContexts']:
            row['nextAction']='核对其他入口或上游翻译是否已生效；相关入口匹配不作为当前入口通过证据'
        if row['kind']=='regex-rule': row['nextAction']='逐条核对真实生成分支及变量；保留未触发分支的原因'; emit('legacy-rules',row)
        if 'disabled-translation-needs-review' in row['flags']: emit('disabled-translations',row)
        if row['remainingLatin']: emit('english-residue',row)
        if row['status']=='preservation-needs-review': emit('preservation-review',row)
        if row['route']=='runtime-capture' and not row['matched'] and row['sourceHasLatin'] and not {'strict-prefix-of-another-capture','visible-prefix-of-another-capture'}.intersection(row['flags']):
            emit('captured-unmatched-candidates',row)
        if row['context']=='dialogue' and not row['matched'] and row['kind']=='text':
            emit('dialogue-unmatched',row)
            dialogue_no_match.setdefault(row['source'],row)
        emit('all-review-items',row)
    for stream in handles.values(): stream.close()
    assert len(seen)==len(original)==manifest['inputRows']==summary['checkedRows'], 'Not every input checked'
    assert sum(len(r['origins']) for r in original.values())==manifest['sourceOccurrences'], 'Lost a source occurrence'
    legacy_lines=list(lines(directory/'legacy-lines.jsonl'))
    by_file=collections.defaultdict(list)
    for entry in legacy_lines: by_file[entry['file']].append(entry)
    for file,records in by_file.items():
        physical_lines=Path(file).read_text(encoding='utf-8-sig').splitlines()
        assert len(records)==len(physical_lines), 'Legacy physical line lost: '+file
        assert {r['line'] for r in records}==set(range(1,len(physical_lines)+1)), 'Legacy physical line duplicated or missing'
        for entry in records:
            raw=physical_lines[entry['line']-1]
            assert hashlib.sha256(raw.encode('utf-8')).hexdigest()==entry['lineSha256'], 'Legacy physical line changed'
    expected_legacy=manifest.get('occurrencesByRoute',{}).get('legacy-source',0)
    assert sum(r['status']=='parsed-source-only' for r in legacy_lines)==expected_legacy, 'Legacy parsed source count mismatch'
    physical=collections.Counter(r['status'] for r in legacy_lines)
    accounting=dict(inputRows=len(original),checkedRows=len(seen),sourceOccurrences=manifest['sourceOccurrences'],
        uniqueSourceStrings=manifest['uniqueSourceStrings'],uniqueVisibleLatinSources=len(latin_sources),
        everyInputHasExactlyOneResult=True,sourceTextAndOriginsUnchanged=True,
        allSourceOccurrencesAccountedFor=True,unknownContextsNotPromotedToPass=True,
        legacyPhysicalLines=dict(physical),statusCounts=dict(statuses),routeCounts=dict(routes),
        queueCounts=dict(queue_counts),unmatchedDialogueUniqueSources=len(dialogue_no_match),
        sourcesWithHistoricalRelatedRecords=len(native_sources),currentNativeVerificationPerformed=False)
    (directory/'accounting.json').write_text(json.dumps(accounting,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    text=['# 英文文本逐条核查\n\n',
        '本报告记录指定输入范围内的全部条目。所有输入均有检查结果，短词、混排、技术字符串、旧规则、停用词条和无法确定用途的内容没有被过滤掉。**这不是全游戏汉化完成证明，也不把静态匹配算作实机通过。**\n\n',
        f'共登记 {manifest["sourceOccurrences"]:,} 个来源记录，合并为 {len(original):,} 个“源文、入口和来源类别”检查项，包含 {manifest["uniqueSourceStrings"]:,} 个不同原文。其中 {len(latin_sources):,} 个不同原文含可见拉丁字母。逐条守恒检查通过：输入、结果、原文和来源对应完整。\n\n',
        '## 检查结果\n\n| 状态 | 检查项数 |\n|---|---:|\n']
    for status,count in statuses.most_common(): text.append(f'| {LABELS.get(status,status)} | {count:,} |\n')
    text.extend(['\n这些数量包含同一源文在不同入口的记录、逐字显示中间态、技术字符串和规则定义，不是玩家可见漏翻条数。输出中的按键名、缩写、变量和普通英文均保留，等待分别确认。\n\n',
        '## 完整台账\n\n',
        '- [全部检查结果](checked.jsonl)：逐条原文、来源、入口、输出、残留英文、数值和图标检查，以及历史原生证据。\n',
        '- [全部待复核项](all-review-items.jsonl)：每条附待查原因和下一步；静态匹配成功也保留原生、译义和排版未验证状态。\n',
        '- [对话入口未匹配](dialogue-unmatched.jsonl)：包括旧对话文件推定入口，仍须确认是否为当前游戏完整句子。\n',
        '- [实际捕获未匹配候选](captured-unmatched-candidates.jsonl)：优先复现这些输入；此视图省略已知严格前缀，完整前缀记录仍在总台账。\n',
        '- [全部英文残留](english-residue.jsonl)、[旧正则逐条记录](legacy-rules.jsonl)、[停用待审词条](disabled-translations.jsonl)、[数值与图标待查](preservation-review.jsonl)。\n',
        '- [输入清单与问题](input-manifest.json)、[逐项守恒检查](accounting.json)、[旧词库所有物理行](legacy-lines.jsonl)、[当前词库哈希](check-summary.json)。\n\n',
        '## 优先处理的对话候选\n\n',
        f'发现 {len(dialogue_no_match):,} 个不同源文在记录或推定的对话入口未匹配。下列为前 30 条索引；完整内容无截断地保存在上述 JSONL。它们尚不等于已经确认的正常游玩漏翻。\n\n'])
    candidates=sorted(dialogue_no_match.values(),key=lambda r:(r['route']!='runtime-capture', len(r['source'])<20, r['id']))
    detailed=['# 对话入口未匹配：完整候选清单\n\n这份清单包含旧对话文件推定入口及提取器声明入口，不等于已确认的正常游玩漏翻。片段、格式串和未知用途文本均保留；每项可通过完整 ID 回到总台账。\n']
    for index,row in enumerate(candidates,1):
        detailed.extend([f'\n## {index}. `{row["id"]}`\n\n',
            f'来源类别：{row["route"]}；入口：{row["context"]}；入口已确认：{row["contextKnown"]}。\n\n',
            '~~~text\n'+row['source']+'\n~~~\n\n',row['nextAction']+'。\n\n',
            '来源：'+json.dumps(row['origins'],ensure_ascii=False)+'\n'])
    (directory/'dialogue-unmatched.md').write_text(''.join(detailed),encoding='utf-8')
    text.append('完整可读版本：[对话候选逐条清单](dialogue-unmatched.md)。\n\n')
    for row in candidates[:30]:
        source=row['source'].replace('\n',' / ').replace('|','\\|')
        text.append(f'- `{row["id"][:12]}`：{source[:220]}'+('…' if len(source)>220 else '')+'\n')
    text.extend(['\n## 边界与未完成事项\n\n',
        '- 字面量、序列化候选和旧文件均不能单独证明实际可见。动态拼接的所有数值、装备组合、条件分支和输入设备变体仍需按生成入口枚举。\n',
        '- 同一源文有旧原生记录时，保留运行编号与结果；历史证据没有自动升级为当前词库或正常游玩验证。\n',
        '- 未确认技术用途的字符串仍在台账中；没有通过通用英文白名单或“非玩家文本”猜测清除。\n',
        '- 未解析来源、解码失败、未知字段结构和缺少原生入口均应保留问题记录。补充提取的范围和失败原因见其独立 manifest。\n',
        '- 本轮没有进行整场实际游玩、逐条译义审校或全部页面排版检查。\n'])
    for supplement in manifest.get('supplementManifests',[]):
        text.append('\n补充提取明细：['+Path(supplement['path']).name+']('+Path(supplement['path']).as_posix()+')。\n\n')
        text.append('提取统计：`'+json.dumps(supplement['summary'],ensure_ascii=False)+'`。\n')
        scope=Path(supplement['path']).parent/'file-scope.json'
        if scope.exists():text.append('\n[文件级未检查范围]('+scope.as_posix()+') 逐项记录未解码文件、原生代码分支和图片文字等边界。\n')
    text.append(f'\n输入问题登记共 {len(manifest.get("issues",[])):,} 项，全部保存在输入 manifest；同一对象的字段解析失败与字节候选结果分别保留。\n')
    (directory/'report.md').write_text(''.join(text),encoding='utf-8')
    print(json.dumps(accounting,ensure_ascii=False))


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('directory',type=Path);args=parser.parse_args();report(args.directory)
