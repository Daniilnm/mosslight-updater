"""Resolve exact public mod downloads; never upload local JARs or own mod hashes."""
import concurrent.futures, hashlib, json, pathlib, urllib.request, urllib.error, sys, time

ROOT = pathlib.Path(r'D:/Games/Minecraft')
INSTANCE = ROOT / 'Instances/Better MC [NEOFORGE] BMC5 _ ______'
OUT = pathlib.Path(__file__).parent / 'source-audit'
OWN = {'bmc5tweaks-1.0.13.jar', 'bmc5-ru-translation-1.1.0.jar', 'backpack-side-gui-bmc5-2.1.0-bmc5.2.jar',
       'dynamic-light-0.7-mc1.21.1.jar'} # CC0-1.0; embedded metadata and author's Modrinth project checked.
PROJECTS = {'create_connected-1.1.13-mc1.21.1.jar': 'create-connected',
 'dimensional_tears-neoforge-1.21.1-0.2.1.jar': 'dimensional-tears',
 'dynamic-light-0.7-mc1.21.1.jar': 'dynamic-light',
 'irons_spellbooks-1.21.1-3.16.3.jar': 'irons-spells-n-spellbooks',
 'kubejs-neoforge-2101.7.2-build.377.jar': 'kubejs',
 'rhino-2101.2.7-build.85.jar': 'rhino', 'schematicon-1.0.0.jar': 'schematicon',
 'sodium-neoforge-0.8.13+mc1.21.1.jar': 'sodium'}

def request(url, body=None):
    req = urllib.request.Request(url, data=None if body is None else json.dumps(body).encode(),
        headers={'User-Agent': 'Mosslight-SourceAudit/1.1 (public mod metadata)', 'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)

def main():
    OUT.mkdir(exist_ok=True)
    metadata = {}
    for path in [ROOT/'Instances/Better MC [NEOFORGE] BMC5/minecraftinstance.json', INSTANCE/'minecraftinstance.json']:
        for a in json.loads(path.read_text(encoding='utf-8-sig'))['installedAddons']:
            f = a.get('installedFile') or {}
            if f.get('fileName'): metadata[f['fileName']] = a
    mods = sorted(p for p in (INSTANCE/'mods').glob('*.jar') if not p.name.lower().startswith('hapticflow'))
    rows, public = [], {}
    for p in mods:
        b = p.read_bytes()
        row = {'path': 'mods/'+p.name, 'sha256': hashlib.sha256(b).hexdigest(), 'sha512': hashlib.sha512(b).hexdigest(), 'size':len(b)}
        a = metadata.get(p.name)
        if p.name in OWN: row['source'] = 'own'
        elif a:
            f = a['installedFile']
            sha1 = next((h['value'].zfill(40) for h in f.get('hashes',[]) if h['type']==1),None)
            if sha1 != hashlib.sha1(b).hexdigest(): raise RuntimeError('Cached file mismatch: '+p.name)
            public[sha1] = row
            row['curseforge'] = {'url':f.get('downloadUrl'), 'distribution':a.get('allowModDistribution'),
                'available': f.get('isAvailable'), 'project':a['addonID'], 'file':f['id']}
        rows.append(row)
    # Only public hashes supplied by CurseForge metadata are sent to Modrinth.
    versions = request('https://api.modrinth.com/v2/version_files', {'hashes':list(public), 'algorithm':'sha1'})
    for sha1,row in public.items():
        for f in versions.get(sha1,{}).get('files',[]):
            if f.get('hashes',{}).get('sha512')==row['sha512'] and f['size']==row['size']:
                row.update(source='modrinth',url=f['url'])
                break
        if not row.get('url') and row['curseforge']['url']:
            row.update(source='curseforge',url=row['curseforge']['url'])
    for row in rows:
        name = pathlib.PurePosixPath(row['path']).name
        if name not in PROJECTS: continue
        try:
            versions = request('https://api.modrinth.com/v2/project/'+PROJECTS[name]+'/version')
            matches = [f for v in versions for f in v['files'] if f['hashes'].get('sha512')==row['sha512'] and f['size']==row['size']]
            if matches: row.update(source='modrinth',url=matches[0]['url'])
        except urllib.error.HTTPError as e: row['lookupError']=str(e)
    (OUT/'sources.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'mods':len(rows),'resolved':sum(bool(r.get('url')) or r.get('source')=='own' for r in rows),
      'unresolved':[r['path'] for r in rows if not r.get('source')],
      'sources':{s:sum(r.get('source')==s for r in rows) for s in ['own','modrinth','curseforge']}},ensure_ascii=False),flush=True)

def verify():
    rows=json.loads((OUT/'sources.json').read_text(encoding='utf-8'))
    dest=OUT/'downloads'; dest.mkdir(exist_ok=True)
    def one(row):
        if not row.get('url'): return
        path=dest/(row['sha256']+'.bin')
        if path.exists() and hashlib.sha256(path.read_bytes()).hexdigest()==row['sha256']:
            row['verified']=True; return
        for attempt in range(3):
            try:
                req=urllib.request.Request(row['url'],headers={'User-Agent':'Mosslight/1.1.0'})
                with urllib.request.urlopen(req,timeout=60) as response:
                    data=response.read(row['size']+1)
                    if len(data)!=row['size'] or hashlib.sha256(data).hexdigest()!=row['sha256']:
                        raise ValueError('Downloaded size/hash mismatch')
                    path.write_bytes(data)
                row['verified']=True; return
            except Exception as e:
                row['downloadError']=str(e)
                time.sleep(attempt+1)
        print('FAILED '+row['path']+': '+row['downloadError'],flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        list(pool.map(one,rows))
    (OUT/'verified-sources.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'verified':sum(bool(r.get('verified')) for r in rows),
      'failed':[{'path':r['path'],'error':r.get('downloadError')} for r in rows if r.get('url') and not r.get('verified')]},ensure_ascii=False),flush=True)

if __name__=='__main__':
    if '--verify' in sys.argv: verify()
    else: main()
