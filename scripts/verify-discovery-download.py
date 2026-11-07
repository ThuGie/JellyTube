"""Opt-in real public discovery/download checks against this project's isolated server, never production."""
import json, pathlib, time, urllib.error, urllib.parse, urllib.request
root=pathlib.Path(__file__).resolve().parents[1];live=root/'artifacts/live';s=json.loads((live/'session.json').read_text());base=s['base'];checks=[]
alice=s['tube-alice'];bob=s['tube-bob'];video='aqz-KE-bpKQ'
def call(path,method='GET',data=None,token=None):
    h={'X-Emby-Token':token or alice['token'],'Content-Type':'application/json'}
    req=urllib.request.Request(base+path,headers=h,method=method,data=None if data is None else json.dumps(data).encode())
    try:
        with urllib.request.urlopen(req,timeout=100) as r:
            raw=r.read();return r.status,json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:return e.code,e.read().decode()[:250]
def check(label,passed):
    checks.append({'check':label,'passed':bool(passed)});print(('PASS ' if passed else 'FAIL ')+label,flush=True)
    (live/'discovery-download-report.json').write_text(json.dumps({'checks':checks},indent=2))
_,settings=call('/JellyTube/Me/Settings');_,admin=call('/JellyTube/Admin/Settings',token=s['token'])
try:
    code,videos=call('/JellyTube/Search?query=Blender');check('managed text video search',code==200 and len(videos.get('items',[]))>0)
    code,channels=call('/JellyTube/Discover?kind=channels&query=Blender');check('managed channel search',code==200 and len(channels.get('items',[]))>0)
    if code==200 and channels['items']:
        c=channels['items'][0];code,_=call('/JellyTube/Me/Channels/'+c['id'],'PUT');check('follow channel',code==204)
        _,mine=call('/JellyTube/Me/Library');_,other=call('/JellyTube/Me/Library',token=bob['token'])
        check('channel follows remain private',any(v['channel']['id']==c['id'] for v in mine['channels']) and not other['channels'])
        code,uploads=call('/JellyTube/Channels/'+c['id']+'/Uploads');check('recent uploads from followed channel',code==200 and len(uploads.get('items',[]))>0)
    code,playlists=call('/JellyTube/Discover?kind=playlists&query='+urllib.parse.quote('Blender open movies'));check('managed public-playlist search',code==200 and len(playlists.get('items',[]))>0)
    if code==200 and playlists['items']:
        code,content=call('/JellyTube/Playlists/'+playlists['items'][0]['id']+'/Videos');check('public-playlist video browsing',code==200 and len(content.get('items',[]))>0)
    denied=dict(admin,downloadDeniedUsers=[bob['id']]);call('/JellyTube/Admin/Settings','PUT',denied,s['token'])
    code,_=call('/JellyTube/Me/Downloads/'+video,'POST',token=bob['token']);check('per-user download permissions enforced',code==403)
    call('/JellyTube/Admin/Settings','PUT',admin,s['token'])
    # Start and cancel a different request; completed download below must remain unaffected.
    cancelId=next(v['id'] for v in videos['items'] if v['id']!=video)
    call('/JellyTube/Me/Downloads/'+cancelId,'POST');code,_=call('/JellyTube/Me/Downloads/'+cancelId,'DELETE');check('cancel download request',code==204)
    call('/JellyTube/Me/Retained/'+video,'DELETE')
    call('/JellyTube/Me/Settings','PUT',dict(settings,maxHeight=360))
    _,before=call('/JellyTube/Videos/'+video+'/Prepare','POST')
    start=time.monotonic();code,_=call('/JellyTube/Me/Downloads/'+video,'POST');check('managed download accepted',code==202)
    previous=None;job=None
    for _ in range(90):
        _,jobs=call('/JellyTube/Me/Downloads');job=next(j for j in jobs if j['id']==video)
        state=(job['status'],job.get('stage'))
        if state!=previous:print('Download stage:',state[0],state[1] or '',flush=True);previous=state
        if job['status'] in ['Ready','Failed','Cancelled']:break
        time.sleep(2)
    check('managed download and Jellyfin merge complete',job['status']=='Ready')
    if job['status']!='Ready':print(job.get('error'))
    else:
        checks[-1]['elapsedSeconds']=round(time.monotonic()-start,3)
        _,after=call('/JellyTube/Videos/'+video+'/Prepare','POST');check('native item identity survives download',before['itemId']==after['itemId'])
        paths=list((live/'data').glob('**/'+video+'/video.strm'));assert paths;url=paths[0].read_text().strip()
        with urllib.request.urlopen(urllib.request.Request(url,headers={'Range':'bytes=0-4095'}),timeout=30) as r:
            data=r.read();check('retained native transport returns a seekable MP4 range',r.status==206 and len(data)==4096 and data[4:8]==b'ftyp')
        _,bob_settings=call('/JellyTube/Me/Settings',token=bob['token'])
        try:
            call('/JellyTube/Me/Settings','PUT',dict(bob_settings,maxHeight=360),bob['token'])
            code,_=call('/JellyTube/Me/Downloads/'+video,'POST',token=bob['token'])
            check('second account can request the same download',code==202)
            for _ in range(30):
                _,other_jobs=call('/JellyTube/Me/Downloads',token=bob['token'])
                other_job=next(j for j in other_jobs if j['id']==video)
                if other_job['status'] in ['Ready','Failed','Cancelled']:break
                time.sleep(1)
            check('second account reuses the matching shared file',other_job['status']=='Ready' and len(list((live/'data').glob('**/.shared/'+video+'-360.mp4')))==1)
            code,_=call('/JellyTube/Me/Retained/'+video,'DELETE',token=bob['token'])
            check('removing one account reference leaves the other playable',code==204 and len(list((live/'data').glob('**/.shared/'+video+'-360.mp4')))==1)
        finally:
            call('/JellyTube/Me/Settings','PUT',bob_settings,bob['token'])
finally:
    call('/JellyTube/Me/Settings','PUT',settings)
    call('/JellyTube/Admin/Settings','PUT',admin,s['token'])
    (live/'discovery-download-report.json').write_text(json.dumps({'checks':checks},indent=2))
raise SystemExit(0 if all(c['passed'] for c in checks) else 1)
