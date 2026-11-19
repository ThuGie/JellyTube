"""Real managed category pages on the isolated server, with settings restored afterward."""
import json, pathlib, time, urllib.request

live = pathlib.Path(__file__).resolve().parents[1] / 'artifacts/live'
session = json.loads((live / 'session.json').read_text())
assert session['base'].startswith('http://127.0.0.1:')

def call(route, method='GET', body=None, admin=False):
    headers = {'X-Emby-Token': session['token'] if admin else session['tube-alice']['token'], 'Content-Type': 'application/json'}
    request = urllib.request.Request(session['base'] + route, method=method, headers=headers,
        data=None if body is None else json.dumps(body).encode())
    with urllib.request.urlopen(request, timeout=30) as response:
        data = response.read()
        return json.loads(data) if data else None

assert call('/System/Info/Public')['Id'] == session['serverId']
settings = call('/JellyTube/Me/Settings')
admin = call('/JellyTube/Admin/Settings', admin=True)
checks = []
try:
    call('/JellyTube/Admin/Settings', 'PUT', dict(admin, searchCallsPerDay=10000, userSearchCallsPerDay=1000), True)
    # Change feed policy to avoid accidentally accepting an old cached snapshot.
    call('/JellyTube/Me/Settings', 'PUT', dict(settings, region='IE', language='en'))
    pages = {}
    for topic in ['music', 'gaming', 'sports', 'travel', 'technology', 'science', 'food']:
        start = time.monotonic()
        page = call('/JellyTube/Me/Feed?kind=explore&topic=' + topic)
        assert len(page['items']) >= 10, topic + ' returned too few videos'
        assert page.get('nextPageToken'), topic + ' lacks continuation'
        pages[topic] = {video['id'] for video in page['items']}
        check = dict(topic=topic, count=len(page['items']), ms=round((time.monotonic()-start)*1000), passed=True)
        checks.append(check); print(json.dumps(check), flush=True)
    overlap = len(pages['technology'] & pages['science'])
    assert overlap < min(len(pages['technology']), len(pages['science'])) / 2
    print('PASS Technology/Science overlap:', overlap, flush=True)
    for topic in ['travel', 'food']:
        first = call('/JellyTube/Me/Feed?kind=explore&topic=' + topic)
        second = call('/JellyTube/Me/Feed?kind=explore&topic=' + topic + '&page=' + first['nextPageToken'])
        assert second['items'] and not pages[topic].intersection(video['id'] for video in second['items'])
        print('PASS continuation without duplicate videos:', topic, flush=True)
finally:
    call('/JellyTube/Me/Settings', 'PUT', settings)
    call('/JellyTube/Admin/Settings', 'PUT', admin, True)
    (live / 'topics-report.json').write_text(json.dumps(checks, indent=2))
