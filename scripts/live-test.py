"""Create an isolated local Jellyfin integration session. Never deploys to the user's server."""
import argparse, json, os, pathlib, secrets, subprocess, time, urllib.request, zipfile

p=argparse.ArgumentParser();p.add_argument('--server',required=True);p.add_argument('--ffmpeg',required=True);a=p.parse_args()
root=pathlib.Path(__file__).resolve().parents[1];live=root/'artifacts/live';live.mkdir(parents=True,exist_ok=True)
if (live/'session.json').exists(): raise SystemExit('An isolated session already exists. Use its recorded server rather than overwriting it.')
subprocess.run(['python',str(root/'scripts/package.py')],check=True)
for name in ['data/plugins/JellyTube','data/plugins/FileTransformation','config','cache','logs']:(live/name).mkdir(parents=True,exist_ok=True)
with zipfile.ZipFile(root/'artifacts/releases/JellyTube_0.1.0.0.zip') as z:z.extractall(live/'data/plugins/JellyTube')
ft=live/'file-transformation.zip'
urllib.request.urlretrieve('https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/download/3.0.3.0/Release-10.11.11.zip',ft)
with zipfile.ZipFile(ft) as z:
    for item in z.infolist():
        if item.filename.endswith(('.dll','meta.json')):(live/'data/plugins/FileTransformation'/pathlib.PurePosixPath(item.filename).name).write_bytes(z.read(item))
(live/'config/network.xml').write_text('''<?xml version="1.0"?><NetworkConfiguration><BaseUrl>/tube-test</BaseUrl><InternalHttpPort>28097</InternalHttpPort><PublicHttpPort>28097</PublicHttpPort><EnableHttps>false</EnableHttps><EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>false</EnableRemoteAccess><AutoDiscovery>false</AutoDiscovery><LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses></NetworkConfiguration>''')
server=pathlib.Path(a.server).resolve();cmd=[str(server),'--datadir',str(live/'data'),'--configdir',str(live/'config'),'--cachedir',str(live/'cache'),'--logdir',str(live/'logs'),'--webdir',str(server.parent/'jellyfin-web'),'--ffmpeg',a.ffmpeg]
process=subprocess.Popen(cmd,stdout=(live/'stdout.log').open('wb'),stderr=(live/'stderr.log').open('wb'),creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
base='http://127.0.0.1:28097/tube-test';token=None
def call(path,method='GET',data=None,auth=None):
    headers={'Content-Type':'application/json','X-Emby-Authorization':'MediaBrowser Client="JellyTubeTests", Device="Isolated", DeviceId="jellytube-isolated", Version="1"'}
    if auth or token:headers['X-Emby-Token']=auth or token
    req=urllib.request.Request(base+path,method=method,headers=headers,data=None if data is None else json.dumps(data).encode())
    with urllib.request.urlopen(req,timeout=100) as r:
        raw=r.read();return json.loads(raw) if raw else None
for attempt in range(90):
    if process.poll() is not None:raise SystemExit('Isolated server exited. Inspect artifacts/live logs.')
    try: info=call('/System/Info/Public');break
    except Exception:time.sleep(1)
else: raise SystemExit('Isolated server did not become ready.')
password=secrets.token_urlsafe(24)
call('/Startup/Configuration','POST',{'UICulture':'en-US','MetadataCountryCode':'NL','PreferredMetadataLanguage':'en'})
call('/Startup/User');call('/Startup/User','POST',{'Name':'tube-admin','Password':password})
call('/Startup/RemoteAccess','POST',{'EnableRemoteAccess':False,'EnableAutomaticPortMapping':False});call('/Startup/Complete','POST')
auth=call('/Users/AuthenticateByName','POST',{'Username':'tube-admin','Pw':password});token=auth['AccessToken']
session=dict(base=base,userId=auth['User']['Id'],token=token,password=password,serverId=info['Id'],pid=process.pid,serverExe=str(server),ffmpeg=a.ffmpeg)
(live/'session.json').write_text(json.dumps(session,indent=2))
settings=call('/JellyTube/Admin/Settings');settings.update(publicBaseUrl=base)
call('/JellyTube/Admin/Settings','PUT',settings)
for name in ['tube-alice','tube-bob']:
    user=call('/Users/New','POST',{'Name':name,'Password':password});uid=user['Id']
    userauth=call('/Users/AuthenticateByName','POST',{'Username':name,'Pw':password})
    session[name]={'id':uid,'token':userauth['AccessToken']}
(live/'session.json').write_text(json.dumps(session,indent=2))
print('Isolated JellyTube test server ready at '+base+'. Credentials saved in ignored artifacts.')
