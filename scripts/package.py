"""Build a portable, versioned plugin archive. Development tool only, never a NAS dependency."""
import argparse, datetime, hashlib, json, os, pathlib, re, subprocess, time, zipfile

root = pathlib.Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser()
p.add_argument('--version')
p.add_argument('--repository', default='ThuGie/JellyTube')
a = p.parse_args()
version = a.version or re.search(r'<Version>([^<]+)</Version>', (root/'src/Jellyfin.Plugin.JellyTube/Jellyfin.Plugin.JellyTube.csproj').read_text()).group(1)
if not re.fullmatch(r'\d+\.\d+\.\d+\.\d+', version) or any(int(n)>65535 for n in version.split('.')): raise SystemExit('Use a four-part assembly version.')
if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', a.repository): raise SystemExit('Use owner/repository.')
subprocess.run(['dotnet','build',str(root/'src/Jellyfin.Plugin.JellyTube'),'-c','Release','--nologo','-p:Version='+version,'-p:AssemblyVersion='+version,'-p:FileVersion='+version,'-p:ContinuousIntegrationBuild=true'],check=True)
epoch=os.environ.get('SOURCE_DATE_EPOCH')
if not epoch:
    r=subprocess.run(['git','log','-1','--format=%ct'],cwd=root,capture_output=True,text=True)
    epoch=r.stdout.strip() if r.returncode==0 else str(int(time.time()))
timestamp=datetime.datetime.fromtimestamp(int(epoch),datetime.timezone.utc)
meta=json.loads((root/'meta.json').read_text());meta.update(version=version,status='Active',timestamp=timestamp.isoformat().replace('+00:00','Z'),imageUrl='https://raw.githubusercontent.com/'+a.repository+'/main/assets/jellytube.png')
files=meta['assemblies'];build=root/'src/Jellyfin.Plugin.JellyTube/bin/Release/net9.0'
out=root/'artifacts/releases';out.mkdir(parents=True,exist_ok=True);archive=out/('JellyTube_'+version+'.zip')
def add(z,name,data):
    info=zipfile.ZipInfo(name,timestamp.timetuple()[:6]);info.compress_type=zipfile.ZIP_DEFLATED;info.create_system=3;info.external_attr=0o100644<<16
    z.writestr(info,data,compress_type=zipfile.ZIP_DEFLATED,compresslevel=9)
with zipfile.ZipFile(archive,'w') as z:
    for name in files:add(z,name,(build/name).read_bytes())
    add(z,'meta.json',(json.dumps(meta,indent=2)+'\n').encode())
    for name in ['README.md','THIRD-PARTY-NOTICES.md','LICENSE','thumb.png']:
        if (root/name).exists():add(z,name,(root/name).read_bytes())
    for folder in ['licenses','docs']:
        for source in sorted((root/folder).glob('*')):
            if source.is_file():add(z,folder+'/'+source.name,source.read_bytes())
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert sorted(n for n in z.namelist() if n.endswith('.dll'))==sorted(files)
    assert not any(n.endswith(('.exe','.so','.dylib')) for n in z.namelist())
    assert json.loads(z.read('meta.json'))['version']==version
(out/(archive.name+'.sha256')).write_text(hashlib.sha256(archive.read_bytes()).hexdigest()+'  '+archive.name+'\n')
print('Created '+str(archive))
