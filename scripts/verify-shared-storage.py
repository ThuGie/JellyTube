"""Shared-file lifecycle checks on the recorded isolated server. Never modifies production."""
import concurrent.futures, hashlib, json, pathlib, shutil, sqlite3, subprocess, time, urllib.error, urllib.request, xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[1]
live = root / 'artifacts/live'
session = json.loads((live / 'session.json').read_text())
assert session['base'].startswith('http://127.0.0.1:')
video = 'aqz-KE-bpKQ'
checks = []

def call(path, method='GET', data=None, account='tube-alice'):
    token = session['token'] if account == 'admin' else session[account]['token']
    request = urllib.request.Request(session['base'] + path, method=method,
        headers={'X-Emby-Token': token, 'Content-Type': 'application/json'},
        data=None if data is None else json.dumps(data).encode())
    try:
        with urllib.request.urlopen(request, timeout=40) as response:
            body = response.read()
            return response.status, json.loads(body) if body else None
    except urllib.error.HTTPError as error:
        return error.code, None

def check(name, condition):
    checks.append({'check': name, 'passed': bool(condition)})
    (live / 'shared-storage-report.json').write_text(json.dumps({'checks': checks}, indent=2))
    print(('PASS ' if condition else 'FAIL ') + name, flush=True)
    assert condition, name

def job(account):
    return next((entry for entry in call('/JellyTube/Me/Downloads', account=account)[1] if entry['id'] == video), None)

def ready(account):
    for _ in range(180):
        current = job(account)
        if current and current['status'] in ('Ready', 'Failed', 'Cancelled'):
            check(account + ' retained copy becomes ready', current['status'] == 'Ready')
            return current
        time.sleep(1)
    raise AssertionError('Download did not complete')

def clean(account):
    current = job(account)
    if current and current['status'] in ('Queued', 'Downloading'):
        call('/JellyTube/Me/Downloads/' + video, 'DELETE', account=account)
    call('/JellyTube/Me/Retained/' + video, 'DELETE', account=account)

def restart():
    version = ET.parse(root / 'src/Jellyfin.Plugin.JellyTube/Jellyfin.Plugin.JellyTube.csproj').findtext('./PropertyGroup/Version')
    subprocess.run(['python', str(root / 'scripts/restart-test.py'), '--archive',
        str(root / 'artifacts/releases' / ('JellyTube_' + version + '.zip'))], check=True)

assert call('/System/Info/Public')[1]['Id'] == session['serverId']
settings = {account: call('/JellyTube/Me/Settings', account=account)[1] for account in ('tube-alice', 'tube-bob')}
media = pathlib.Path(call('/JellyTube/Admin/Settings', account='admin')[1]['mediaRoot']).resolve()
assert media.is_relative_to(live.resolve()), 'Only isolated test media can be modified'
shared = media / '.shared'
path = shared / (video + '-360.mp4')
try:
    for account in settings:
        clean(account)
        call('/JellyTube/Me/Settings', 'PUT', dict(settings[account], maxHeight=360), account)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        requests = [pool.submit(call, '/JellyTube/Me/Downloads/' + video, 'POST', None, account) for account in settings]
        check('matching concurrent download requests are accepted', all(request.result()[0] == 202 for request in requests))
    check('one matching account can cancel independently', call('/JellyTube/Me/Downloads/' + video, 'DELETE')[0] == 204)
    ready('tube-bob')
    check('cancelled account remains cancelled', job('tube-alice')['status'] == 'Cancelled')
    check('matching requests publish one shared file', path.exists() and len(list(shared.glob(video + '-360.mp4'))) == 1)
    code, retained = call('/JellyTube/Me/Downloads/' + video, 'POST')
    check('late subscriber reuses a published file immediately', code == 202 and retained['status'] == 'Ready')
    saved_job = job('tube-alice')
    # Keep a legacy fixture for the migration test, before releasing the last shared reference.
    legacy = media / '.retained' / session['tube-alice']['id'].replace('-', '').lower() / (video + '.mp4')
    assert legacy.resolve().is_relative_to(media)
    legacy.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(path, legacy)
    digest = hashlib.sha256(legacy.read_bytes()).hexdigest()
    code, start = call('/JellyTube/Videos/' + video + '/Start', 'POST')
    check('retained direct playback selects the local file', code == 200 and start['source']['mode'] == 'mp4')
    stream = urllib.request.urlopen(session['base'] + '/' + start['source']['path'], timeout=20)
    try:
        stream.read(1)
        check('removing one reference preserves shared bytes', call('/JellyTube/Me/Retained/' + video, 'DELETE', account='tube-bob')[0] == 204 and path.exists())
        check('active playback retains bytes after the final reference is removed', call('/JellyTube/Me/Retained/' + video, 'DELETE')[0] == 204 and path.exists())
    finally:
        stream.close()
    for _ in range(50):
        if not path.exists(): break
        time.sleep(.1)
    check('aborting playback releases the final shared file', not path.exists())
    databases = list((live / 'data').glob('**/jellytube.db'))
    assert len(databases) == 1
    owner = session['tube-alice']['id'].replace('-', '').lower()
    # Simulate the old plugin's retained entry. SQLite writes remain inside the verified isolated data directory.
    with sqlite3.connect(databases[0]) as db:
        asset = json.loads(db.execute("SELECT json FROM records WHERE owner=? AND kind='asset' AND id=?", (owner, video)).fetchone()[0])
        asset.update(localFile=str(legacy), retained=True)
        for kind, value in [('asset', asset), ('download', saved_job)]:
            db.execute('INSERT OR REPLACE INTO records(owner,kind,id,json) VALUES(?,?,?,?)', (owner, kind, video, json.dumps(value)))
    restart()
    check('legacy copy can be requested by another account', call('/JellyTube/Me/Downloads/' + video, 'POST', account='tube-bob')[0] == 202)
    ready('tube-bob')
    check('legacy bytes are migrated and the old file is removed', path.exists() and not legacy.exists() and hashlib.sha256(path.read_bytes()).hexdigest() == digest)
    clean('tube-alice'); clean('tube-bob')
    call('/JellyTube/Me/Settings', 'PUT', dict(settings['tube-bob'], maxHeight=480), 'tube-bob')
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        requests = [pool.submit(call, '/JellyTube/Me/Downloads/' + video, 'POST', None, account) for account in settings]
        check('different quality requests are accepted', all(request.result()[0] == 202 for request in requests))
    ready('tube-alice'); ready('tube-bob')
    check('different quality requests retain separate files', path.exists() and (shared / (video + '-480.mp4')).exists())
    # An interrupted job with a fully published file must recover without losing the private reference.
    with sqlite3.connect(databases[0]) as db:
        owner = session['tube-bob']['id'].replace('-', '').lower()
        row = db.execute("SELECT json FROM records WHERE owner=? AND kind='download' AND id=?", (owner, video)).fetchone()
        interrupted = json.loads(row[0]); interrupted['status'] = 'Downloading'
        db.execute("UPDATE records SET json=? WHERE owner=? AND kind='download' AND id=?", (json.dumps(interrupted), owner, video))
    restart(); ready('tube-bob')
    check('restart preserves separate account qualities', job('tube-alice')['maxHeight'] == 360 and job('tube-bob')['maxHeight'] == 480)
finally:
    for account in settings:
        clean(account)
        call('/JellyTube/Me/Settings', 'PUT', settings[account], account)
