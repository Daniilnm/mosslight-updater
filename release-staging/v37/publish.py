"""Publish the already-signed v37 artifacts; never reads or generates a private key.

Idempotent: existing assets must match, old channels are renamed, never deleted.
Run --preflight for read-only checks; writes require --publish in the exact repo.
"""
import base64, gzip, hashlib, json, os, subprocess, sys, tempfile, time
import urllib.error, urllib.parse, urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = 'Daniilnm/mosslight-updater'
API = 'https://api.github.com/repos/' + REPO
BACKUP = 'install-channel-v36-backup-2026-10-21.json'
MAIN = '8b29ffa7236cb88382071872a73627d96fd6e003'
HEADERS = {'User-Agent': 'Mosslight-v37-Publisher', 'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28'}

def sha(b): return hashlib.sha256(b).hexdigest()

def request(url, *, method='GET', data=None, auth=False, raw=False):
    # Tokens are sent only to GitHub's API/upload hosts, never to download redirects.
    if auth:
        assert urllib.parse.urlsplit(url).hostname in ('api.github.com', 'uploads.github.com')
    headers = dict(HEADERS)
    if auth:
        token = os.environ.get('GH_TOKEN')
        if not token: raise RuntimeError('No Actions publication credential')
        headers['Authorization'] = 'Bearer ' + token
    if data is not None:
        if raw: headers['Content-Type'] = 'application/json'
        else:
            data = json.dumps(data, ensure_ascii=False).encode('utf-8')
            headers['Content-Type'] = 'application/json'
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=data, method=method, headers=headers), timeout=45) as response:
            b = response.read()
            return b if raw else json.loads(b)
    except urllib.error.HTTPError as e:
        if e.code == 404 and method == 'GET': return None
        raise RuntimeError(f'GitHub HTTP {e.code} during {method} {urllib.parse.urlsplit(url).path}') from None

def public_bytes(url):
    # All artifact URLs are fixed public releases URLs in this repository.
    assert url.startswith('https://github.com/' + REPO + '/releases/download/')
    return request(url, raw=True)

def signed(data):
    envelope = json.loads(data)
    payload = base64.b64decode(envelope['payload'], validate=True)
    signature = base64.b64decode(envelope['signature'], validate=True)
    try:
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import padding
    except ImportError:
        # Ubuntu's built-in OpenSSL; no installation or private key required.
        with tempfile.TemporaryDirectory() as folder:
            p = Path(folder)
            (p / 'payload').write_bytes(payload)
            (p / 'signature').write_bytes(signature)
            subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(HERE / 'release-public.pem'),
                            '-signature', str(p / 'signature'), str(p / 'payload')], check=True,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    else:
        key = serialization.load_pem_public_key((HERE / 'release-public.pem').read_bytes())
        key.verify(signature, payload, padding.PKCS1v15(), hashes.SHA256())
    if envelope['format'] == 'gzip': payload = gzip.decompress(payload)
    else: assert envelope['format'] == 'json'
    return json.loads(payload)

def assets(release): return {a['name']: a for a in release['assets']}

def verify_asset(asset, data):
    assert asset['state'] == 'uploaded'
    assert asset['size'] == len(data)
    assert asset['digest'] == 'sha256:' + sha(data), 'Remote asset hash mismatch: ' + asset['name']

def download_matches(asset, expected, attempts=18):
    for i in range(attempts):
        data = public_bytes(asset['browser_download_url'])
        if data == expected:
            verify_asset(asset, data)
            return data
        if i + 1 < attempts: time.sleep(10)
    raise RuntimeError('Public download did not match expected bytes: ' + asset['name'])

def upload(release, name, data):
    current = request(API + '/releases/' + str(release['id']), auth=True)
    found = assets(current).get(name)
    if found:
        verify_asset(found, data)
        return found
    url = release['upload_url'].split('{')[0] + '?name=' + urllib.parse.quote(name)
    result = request(url, method='POST', data=data, auth=True, raw=True)
    asset = json.loads(result)
    verify_asset(asset, data)
    return asset

def inputs():
    manifest = json.loads((HERE / 'expected-sha256.json').read_bytes())
    for name, digest in manifest.items():
        assert sha((HERE / name).read_bytes()) == digest, 'Local artifact changed: ' + name
    pack_data = (HERE / 'pack.json').read_bytes()
    channel_data = (HERE / 'install-channel.json').read_bytes()
    old_data = (HERE / 'previous-install-channel.json').read_bytes()
    legacy_data = (HERE / 'legacy-channel.json').read_bytes()
    pack, channel, old, legacy = map(signed, [pack_data, channel_data, old_data, legacy_data])
    assert pack['version'] == channel['packVersion'] == 37
    assert old['packVersion'] == 36 and legacy['packVersion'] == 22
    assert channel['appVersion'] == old['appVersion'] == legacy['appVersion'] == '1.1.1'
    assert channel['repository'] == REPO
    assert channel['packUrl'] == 'https://github.com/' + REPO + '/releases/download/pack-v37/pack.json'
    assert channel['appUrl'] == old['appUrl'] == legacy['appUrl']
    return pack_data, channel_data, old_data, legacy_data, pack, old

def preflight():
    pack_data, channel_data, old_data, legacy_data, pack, old = inputs()
    main = request(API + '/git/ref/heads/main')
    assert main['object']['sha'] == MAIN, 'Repository main changed; review target before publishing'
    prior = public_bytes(old['packUrl'])
    assert prior is not None
    previous_pack = signed(prior)
    assert previous_pack['version'] == 36 and previous_pack['files'] == pack['files']
    latest = request(API + '/releases/latest')
    assert latest['tag_name'] == 'app-v1.1.1'
    stable = request(API + '/releases/tags/stable')
    assert stable and not stable['draft'] and not stable.get('immutable', False)
    a = assets(stable)
    download_matches(a['channel.json'], legacy_data)
    current = a.get('install-channel.json')
    if current:
        assert current['digest'] in ('sha256:' + sha(old_data), 'sha256:' + sha(channel_data)), 'Active channel changed concurrently'
        # Avoid the cached stable filename: digest + previous signed release are authoritative.
    else:
        assert BACKUP in a, 'Channel missing without known backup'
    if BACKUP in a: download_matches(a[BACKUP], old_data)
    existing = request(API + '/releases/tags/pack-v37', auth=bool(os.environ.get('GH_TOKEN')))
    if existing:
        assert existing['tag_name'] == 'pack-v37'
        existing_asset = assets(existing).get('pack.json')
        if existing_asset: verify_asset(existing_asset, pack_data)
    print('PREFLIGHT_PASS signatures, 709 unchanged client files, v36 channel, legacy v22, Latest app1.1.1', flush=True)
    return pack_data, channel_data, old_data, legacy_data, stable, existing

def publish():
    assert os.environ.get('GITHUB_REPOSITORY') == REPO
    assert os.environ.get('GITHUB_REF') == 'refs/heads/release/publish-v37'
    pack_data, channel_data, old_data, legacy_data, stable, release = preflight()
    if release is None:
        release = request(API + '/releases', method='POST', auth=True, data={
            'tag_name': 'pack-v37', 'target_commitish': MAIN,
            'name': 'Mosslight 0.137 — исключения Create для Does It Tick?',
            'body': 'На сервере расширены исключения Does It Tick?: 37 модулей Create, аддонов и физики Aeronautics / Simulated / Offroad / Sable. Оптимизация остальных модов сохранена. Сервер перезапущен.\n\nПравка направлена на потерю тяги после ухода от корабля; результат ещё требует проверки в игре.\n\nКлиентские моды и Mosslight Update 1.1.1 прежние. Каталог содержит те же 709 клиентских файлов, что v36; повторная загрузка модов не нужна. Конфиг Does It Tick? применяется на сервере.',
            'draft': True, 'prerelease': False, 'make_latest': 'false'})
    pack_asset = upload(release, 'pack.json', pack_data)
    if release['draft']:
        release = request(API + '/releases/' + str(release['id']), method='PATCH', auth=True,
                          data={'draft': False, 'make_latest': 'false'})
    assert not release['draft']
    download_matches(pack_asset, pack_data)
    print('PACK_V37_PUBLISHED_AND_PUBLICLY_VERIFIED', flush=True)
    # Re-read immediately before the channel mutation. Never delete/overwrite an asset.
    stable = request(API + '/releases/tags/stable', auth=True)
    a = assets(stable)
    download_matches(a['channel.json'], legacy_data)
    current = a.get('install-channel.json')
    if current and current['digest'] == 'sha256:' + sha(old_data):
        assert BACKUP not in a, 'Backup already exists alongside old active channel; review manually'
        renamed = request(API + '/releases/assets/' + str(current['id']), method='PATCH', auth=True,
                          data={'name': BACKUP})
        download_matches(renamed, old_data)
        print('PREVIOUS_V36_CHANNEL_PRESERVED', flush=True)
    elif current:
        verify_asset(current, channel_data)
        assert BACKUP in a
        download_matches(a[BACKUP], old_data)
    else:
        assert BACKUP in a
        download_matches(a[BACKUP], old_data)
    new = upload(stable, 'install-channel.json', channel_data)
    download_matches(new, channel_data)
    final_stable = request(API + '/releases/tags/stable')
    final = assets(final_stable)
    download_matches(final['channel.json'], legacy_data)
    download_matches(final[BACKUP], old_data)
    assert request(API + '/releases/latest')['tag_name'] == 'app-v1.1.1'
    # Keep stable's visible description consistent with its active signed channel.
    request(API + '/releases/' + str(stable['id']), method='PATCH', auth=True, data={
        'body': 'Текущая сборка: Mosslight v37. На сервере добавлены исключения Create / Aeronautics / Simulated / Offroad / Sable для Does It Tick?. Проверка пропеллеров после ухода от корабля ещё требуется в игре.\n\ninstall-channel.json → v37 / Mosslight Update 1.1.1. Совместимый channel.json остаётся на v22 / app1.1.1. Резервные копии и прежние выпуски сохранены. Клиентские файлы v37 совпадают с v36; заново скачивать моды не требуется.',
        'make_latest': 'false'})
    proof = {'status': 'PASS', 'pack': 37, 'app': '1.1.1', 'release_url': release['html_url'],
             'pack_sha256': sha(pack_data), 'channel_sha256': sha(channel_data),
             'legacy_channel_unchanged': True, 'backup': BACKUP, 'latest': 'app-v1.1.1',
             'publication_method': 'GitHub Actions; existing signed artifacts; no private key uploaded'}
    (HERE / 'publication-proof.json').write_text(json.dumps(proof, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(proof, ensure_ascii=False), flush=True)

if __name__ == '__main__':
    if sys.argv[1:] == ['--preflight']: preflight()
    elif sys.argv[1:] == ['--publish']: publish()
    else: raise SystemExit('Use --preflight or --publish')
