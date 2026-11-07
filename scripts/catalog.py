"""Generate/merge Jellyfin catalog metadata from the actual published archive."""
import argparse, datetime, hashlib, json, os, pathlib, re, subprocess, zipfile

ROOT=pathlib.Path(__file__).resolve().parents[1]
GUID='241ac687-789b-4499-8ba1-9fb16602b421'
def version(value):
    if not re.fullmatch(r'\d+\.\d+\.\d+\.\d+',value) or any(int(n)>65535 for n in value.split('.')): raise ValueError('Use a four-part numeric assembly version.')
    return value
def key(value): return tuple(map(int,value.split('.')))
def written_notes(value):
    text=(ROOT/'CHANGELOG.md').read_text(encoding='utf-8')
    match=re.search(r'^## '+re.escape(value)+r'\s*\n(.*?)(?=^## |\Z)',text,re.M|re.S)
    return match.group(1).strip() if match else ''
def notes(value):
    if written := written_notes(value): return written
    tags = subprocess.run(['git','tag','--merged','HEAD','--list','v*'],cwd=ROOT,capture_output=True,text=True).stdout.splitlines()
    previous = max((tag for tag in tags if re.fullmatch(r'v\d+\.\d+\.\d+\.\d+',tag) and key(tag[1:])<key(value)),
                   key=lambda tag:key(tag[1:]),default=None)
    revision = previous+'..HEAD' if previous else 'HEAD'
    log = subprocess.run(['git','log','--no-merges','--format=%s',revision],cwd=ROOT,capture_output=True,text=True).stdout
    subjects = [line.strip() for line in log.splitlines() if line.strip() and not line.startswith('chore: publish Jellyfin catalog')]
    return '\n'.join('- '+subject for subject in subjects[:12]) or 'Release '+value
def entry(archive,repository,value,timestamp=None):
    version(value)
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+',repository): raise ValueError('Invalid repository.')
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None
        meta=json.loads(z.read('meta.json'))
        assert meta['guid']==GUID and meta['version']==value
        assert sorted(meta['assemblies'])==sorted(n for n in z.namelist() if n.endswith('.dll'))
        assert not any(n.endswith(('.exe','.so','.dylib')) for n in z.namelist())
        assert 'thumb.png' in z.namelist()
    record={k:meta[k] for k in ['guid','name','description','overview','owner','category','imageUrl']}
    record['versions']=[dict(version=value,changelog=notes(value),targetAbi=meta['targetAbi'],
        sourceUrl=f'https://github.com/{repository}/releases/download/v{value}/JellyTube_{value}.zip',
        checksum=hashlib.md5(pathlib.Path(archive).read_bytes(),usedforsecurity=False).hexdigest(),
        timestamp=timestamp or meta['timestamp'],dependencies=[])]
    return record
def merge(incoming):
    path=ROOT/'manifest.json';history=json.loads(path.read_text()) if path.exists() else []
    assert incoming['guid']==GUID and len(incoming['versions'])==1
    new=incoming['versions'][0]
    previous=next((item for item in history if item['guid']==GUID),None)
    versions=previous['versions'] if previous else []
    for release in versions:
        if release.get('changelog') == 'Release '+release['version'] and (written := written_notes(release['version'])):
            release['changelog'] = written
    existing=next((v for v in versions if v['version']==new['version']),None)
    if existing and existing['checksum']!=new['checksum']:raise ValueError('Refusing to replace a published version with different bytes. Bump the version.')
    incoming['versions']=sorted(([new] if not existing else [])+versions,key=lambda v:key(v['version']),reverse=True)
    history=[item for item in history if item['guid']!=GUID]+[incoming]
    path.write_text(json.dumps(history,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
    meta_path=ROOT/'meta.json';meta=json.loads(meta_path.read_text())
    if key(new['version'])>=key(meta['version']):
        meta.update(version=new['version'],timestamp=new['timestamp'],changelog=new['changelog'])
        meta_path.write_text(json.dumps(meta,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
        build_path=ROOT/'build.yaml';build=json.loads(build_path.read_text());build.update(version=new['version'],timestamp=new['timestamp'])
        build_path.write_text(json.dumps(build,indent=2)+'\n',encoding='utf-8')
        project=ROOT/'src/Jellyfin.Plugin.JellyTube/Jellyfin.Plugin.JellyTube.csproj'
        project.write_text(re.sub(r'<Version>[^<]+</Version>','<Version>'+new['version']+'</Version>',project.read_text()),encoding='utf-8')
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('command',choices=['resolve','entry','merge']);p.add_argument('--version');p.add_argument('--archive');p.add_argument('--repository');p.add_argument('--output');p.add_argument('--input');p.add_argument('--timestamp');a=p.parse_args()
    if a.command=='resolve':
        value=version(a.version or (os.environ.get('INPUT_VERSION') if os.environ.get('GITHUB_EVENT_NAME')=='workflow_dispatch' else os.environ.get('GITHUB_REF_NAME','').removeprefix('v')))
        output='version='+value+'\ntag=v'+value+'\n'
        if os.environ.get('GITHUB_OUTPUT'):
            with open(os.environ['GITHUB_OUTPUT'],'a') as f:f.write(output)
        print(output,end='')
    elif a.command=='entry':pathlib.Path(a.output).write_text(json.dumps(entry(a.archive,a.repository,a.version,a.timestamp),indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
    else:merge(json.loads(pathlib.Path(a.input).read_text()))
