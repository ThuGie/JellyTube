"""Repeatable integration checks against this project's isolated server only. Writes no credentials to the report."""
import json, pathlib, subprocess, time, urllib.error, urllib.request, xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[1];live=root/'artifacts/live';s=json.loads((live/'session.json').read_text());base=s['base'];checks=[]
def call(path,method='GET',data=None,token=None):
    headers={'Content-Type':'application/json','X-Emby-Token':token or s['token']}
    req=urllib.request.Request(base+path,headers=headers,method=method,data=None if data is None else json.dumps(data).encode())
    try:
        with urllib.request.urlopen(req,timeout=100) as r:
            raw=r.read();return r.status,json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:return e.code,e.read().decode()[:300]
def check(label,condition):
    checks.append({'check':label,'passed':bool(condition)});print(('PASS ' if condition else 'FAIL ')+label,flush=True)
    (live/'integration-report.json').write_text(json.dumps({'checks':checks},indent=2))
code,settings=call('/JellyTube/Admin/Settings');check('plugin administrator endpoint loads',code==200)
if code!=200:print(settings);raise SystemExit(1)
settings.update(publicBaseUrl=base);code,_=call('/JellyTube/Admin/Settings','PUT',settings);check('administrator settings save',code==204)
for name in ['tube-alice','tube-bob']:
    if name in s:continue
    _,allusers=call('/Users');existing=next((u for u in allusers if u['Name']==name),None)
    if existing is None:_,existing=call('/Users/New','POST',{'Name':name,'Password':s['password']})
    code,auth=call('/Users/AuthenticateByName','POST',{'Username':name,'Pw':s['password']});assert code==200,(code,auth)
    s[name]={'id':existing['Id'],'token':auth['AccessToken']};(live/'session.json').write_text(json.dumps(s,indent=2))
alice=s['tube-alice'];bob=s['tube-bob']
code,binding=call('/JellyTube/Admin/Library','POST');check('one shared library provisioned',code==200)
if code!=200:print(binding);raise SystemExit(1)
for name,who in [('Alice',alice),('Bob',bob)]:
    who['libraryId']=binding['libraryId']
    code,views=call('/Users/'+who['id']+'/Views',token=who['token'])
    ids=[v['Id'].replace('-','') for v in views.get('Items',[])]
    check(name+' sees the same shared library',code==200 and binding['libraryId'].replace('-','') in ids)
import urllib.parse
for representation in ['body','query']:
    name='JellyTube-overlap-'+representation;path='/Library/VirtualFolders?name='+name+'&refreshLibrary=false'
    body={'LibraryOptions':{'PathInfos':[{'Path':settings['mediaRoot']}]}}
    if representation=='query':path+='&paths='+urllib.parse.quote(settings['mediaRoot'],safe='');body={}
    code,_=call(path,'POST',body);check('shared parent library rejected via '+representation,code==400)
    if code==204:call('/Library/VirtualFolders?name='+name+'&refreshLibrary=false','DELETE')
_,folders=call('/Library/VirtualFolders');owned=next(f for f in folders if f['ItemId'].replace('-','')==alice['libraryId'].replace('-',''))
code,_=call('/Library/VirtualFolders/LibraryOptions','POST',{'Id':alice['libraryId'],'LibraryOptions':owned['LibraryOptions']});check('shared-library options remain editable',code==204)
_,allusers=call('/Users');newuser=next((u for u in allusers if u['Name']=='tube-newbie'),None)
if newuser is None:_,newuser=call('/Users/New','POST',{'Name':'tube-newbie','Password':s['password']})
_,newviews=call('/Users/'+newuser['Id']+'/Views')
check('new users with all-library access see JellyTube',any(v['Name']=='JellyTube' for v in newviews.get('Items',[])))
code,_=call('/JellyTube/Admin/Health',token=alice['token']);check('non-admin cannot access administration',code==403)
video='aqz-KE-bpKQ'
code,data=call('/JellyTube/Search?query='+video,token=alice['token']);check('managed video metadata lookup',code==200 and data['items'][0]['id']==video)
code,_=call('/JellyTube/Me/Favorites/'+video,'PUT',token=alice['token']);check('save favorite',code==204)
_,a=call('/JellyTube/Me/Library',token=alice['token']);_,b=call('/JellyTube/Me/Library',token=bob['token']);check('favorites are private',any(v['video']['id']==video for v in a['favorites']) and not any(v['video']['id']==video for v in b['favorites']))
_,playlist=call('/JellyTube/Me/Playlists','POST',{'name':'Alice private test'},alice['token'])
code,_=call('/JellyTube/Me/Playlists/'+playlist['id'],'PUT',playlist,bob['token']);check('other user cannot edit playlist',code==409)
code,item=call('/JellyTube/Videos/'+video+'/Prepare','POST',token=alice['token']);check('native preparation accepted',code==200)
if code!=200:print(item);raise SystemExit(1)
for _ in range(35):
    if item.get('itemId'):break
    time.sleep(2);code,item=call('/JellyTube/Videos/'+video+'/Prepare','POST',token=alice['token'])
check('native video indexed',code==200 and bool(item.get('itemId')))
if not item.get('itemId'):raise SystemExit(1)
s['testItemId']=item['itemId'];(live/'session.json').write_text(json.dumps(s,indent=2))
code,ownerItem=call('/Users/'+alice['id']+'/Items/'+item['itemId'],token=alice['token']);check('owner can open native item',code==200)
code,other=call('/JellyTube/Videos/'+video+'/Prepare','POST',token=bob['token'])
check('both accounts prepare the same native item',code==200 and other.get('itemId')==item['itemId'])
code,otherItem=call('/Users/'+bob['id']+'/Items/'+item['itemId'],token=bob['token'])
check('shared item keeps native favorite state separate',code==200 and not otherItem.get('UserData',{}).get('IsFavorite'))
call('/JellyTube/Me/Library',token=alice['token']);_,nativeItem=call('/Users/'+alice['id']+'/Items/'+item['itemId'],token=alice['token'])
check('favorite appears in native Jellyfin user data',nativeItem.get('UserData',{}).get('IsFavorite'))
call('/UserFavoriteItems/'+item['itemId']+'?userId='+alice['id'],'DELETE',token=alice['token'])
_,personal=call('/JellyTube/Me/Library',token=alice['token']);check('native favorite changes synchronize back',not any(v['video']['id']==video for v in personal['favorites']))
call('/JellyTube/Me/Favorites/'+video,'PUT',token=alice['token'])
profile={'Name':'JellyTube integration','MaxStreamingBitrate':20000000,'DirectPlayProfiles':[],
         'TranscodingProfiles':[{'Container':'ts','Type':'Video','VideoCodec':'h264','AudioCodec':'aac','Protocol':'hls','Context':'Streaming','MaxAudioChannels':'2','MinSegments':1,'BreakOnNonKeyFrames':False}],
         'CodecProfiles':[],'SubtitleProfiles':[]}
code,playback=call('/Items/'+item['itemId']+'/PlaybackInfo','POST',{'UserId':alice['id'],'StartTimeTicks':0,'IsPlayback':True,'AutoOpenLiveStream':True,'DeviceProfile':profile},alice['token'])
check('Jellyfin native playback negotiation',code==200 and bool(playback.get('MediaSources')))
if code!=200:print(playback);raise SystemExit(1)
source=playback['MediaSources'][0];url=source['Path']
if url.endswith('.strm'):url=pathlib.Path(url).read_text().strip()
if not url.startswith(base):
    strms=list((live/'data/plugins/configurations').glob('**/*.strm'))
    roots=list((live/'data').glob('**/'+video+'/video.strm'));assert roots;url=roots[0].read_text().strip()
try:
    _,jobs=call('/JellyTube/Me/Downloads',token=alice['token'])
    if any(j['id']==video and j['status']=='Ready' for j in jobs):
        with urllib.request.urlopen(urllib.request.Request(url,headers={'Range':'bytes=0-4095'}),timeout=30) as r:
            check('retained copy streams with native byte ranges',r.status==206 and len(r.read())==4096)
        code,_=call('/JellyTube/Me/Retained/'+video,'DELETE',token=alice['token']);check('explicit retained-copy removal',code==204)
    _,jobs=call('/JellyTube/Me/Downloads',token=bob['token'])
    if any(j['id']==video and j['status']=='Ready' for j in jobs):
        code,_=call('/JellyTube/Me/Retained/'+video,'DELETE',token=bob['token']);check('last shared copy can be removed',code==204)
    with urllib.request.urlopen(url,timeout=90) as r:manifest=r.read();check('managed DASH manifest serves',r.status==200)
    doc=ET.fromstring(manifest);ns={'d':'urn:mpeg:dash:schema:mpd:2011'};tracks=doc.findall('.//d:BaseURL',ns);check('manifest includes audio and video',len(tracks)==2)
    req=urllib.request.Request(tracks[0].text,headers={'Range':'bytes=0-4095'})
    with urllib.request.urlopen(req,timeout=30) as r:check('native transport supports byte ranges',r.status==206 and len(r.read())==4096)
    _,personalSettings=call('/JellyTube/Me/Settings',token=alice['token']);updatedSettings=dict(personalSettings,maxHeight=360)
    call('/JellyTube/Me/Settings','PUT',updatedSettings,alice['token'])
    with urllib.request.urlopen(req,timeout=30) as r:check('active stream stays valid after changing preferred quality',r.status==206 and len(r.read())==4096)
    call('/JellyTube/Me/Settings','PUT',personalSettings,alice['token'])
    start=time.monotonic()
    result=subprocess.run([s['ffmpeg'],'-hide_banner','-loglevel','error','-ss','120','-i',url,'-t','3','-map','0:v:0','-map','0:a:0','-c','copy','-f','null','-'],capture_output=True,timeout=100)
    check('Jellyfin FFmpeg reads both tracks and seeks to 120s',result.returncode==0)
    checks[-1]['elapsedSeconds']=round(time.monotonic()-start,3)
    if result.returncode:print(result.stderr.decode(errors='replace')[-1200:])
except Exception as e:check('managed native transport completes',False);print(type(e).__name__,str(e)[:300])
report={'serverVersion':call('/System/Info/Public')[1]['Version'],'checks':checks}
(live/'integration-report.json').write_text(json.dumps(report,indent=2))
raise SystemExit(0 if all(c['passed'] for c in checks) else 1)
