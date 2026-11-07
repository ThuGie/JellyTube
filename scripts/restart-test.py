"""Restart only this project's recorded isolated server; never touches a production Jellyfin instance."""
import argparse, json, os, pathlib, signal, subprocess, time, urllib.request, zipfile
parser=argparse.ArgumentParser();parser.add_argument('--without-file-transformation',action='store_true');args=parser.parse_args()
root=pathlib.Path(__file__).resolve().parents[1];live=root/'artifacts/live';s=json.loads((live/'session.json').read_text())
info=json.load(urllib.request.urlopen(s['base']+'/System/Info/Public',timeout=3));assert info['Id']==s['serverId']
if os.name=='nt':
    cmdline=subprocess.check_output(['powershell','-NoProfile','-Command',f"(Get-CimInstance Win32_Process -Filter 'ProcessId = {s['pid']}').CommandLine"],text=True)
else:cmdline=pathlib.Path(f"/proc/{s['pid']}/cmdline").read_bytes().decode().replace('\0',' ')
assert str(live).casefold() in cmdline.casefold() and 'jellyfin' in cmdline.casefold()
subprocess.run(['python',str(root/'scripts/package.py')],check=True)
os.kill(s['pid'],signal.SIGTERM)
for _ in range(50):
    try:urllib.request.urlopen(s['base']+'/System/Info/Public',timeout=.2);time.sleep(.2)
    except Exception:break
if args.without_file_transformation:
    dll=live/'data/plugins/FileTransformation/Jellyfin.Plugin.FileTransformation.dll';disabled=dll.with_suffix('.dll.disabled')
    assert dll.resolve().is_relative_to(live.resolve()) and disabled.resolve().is_relative_to(live.resolve())
    for _ in range(50):
        try:
            if dll.exists():dll.replace(disabled)
            break
        except PermissionError:time.sleep(.2)
for attempt in range(50):
    try:
        with zipfile.ZipFile(root/'artifacts/releases/JellyTube_0.1.0.0.zip') as z:z.extractall(live/'data/plugins/JellyTube')
        break
    except PermissionError:time.sleep(.2)
server=pathlib.Path(s['serverExe']);command=[str(server),'--datadir',str(live/'data'),'--configdir',str(live/'config'),'--cachedir',str(live/'cache'),'--logdir',str(live/'logs'),'--webdir',str(server.parent/'jellyfin-web'),'--ffmpeg',s['ffmpeg']]
p=subprocess.Popen(command,stdout=(live/'restart.stdout.log').open('wb'),stderr=(live/'restart.stderr.log').open('wb'),creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
s['pid']=p.pid;(live/'session.json').write_text(json.dumps(s,indent=2))
for _ in range(60):
    if p.poll() is not None:raise SystemExit('Isolated server exited; check restart logs.')
    try:
        info=json.load(urllib.request.urlopen(s['base']+'/System/Info/Public',timeout=1))
        if info['Id']==s['serverId']:break
    except Exception:pass
    time.sleep(1)
else:raise SystemExit('Isolated restart timed out.')
print('Isolated test server restarted.')
