"""Deterministic release/catalog checks; no network or NAS runtime dependencies."""
import hashlib, importlib.util, json, pathlib, tempfile, unittest, zipfile

ROOT=pathlib.Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('catalog',ROOT/'scripts/catalog.py');catalog=importlib.util.module_from_spec(spec);spec.loader.exec_module(catalog)
class CatalogTests(unittest.TestCase):
    def test_version_rejects_commands_and_incomplete_versions(self):
        self.assertEqual('0.1.0.0',catalog.version('0.1.0.0'))
        for value in ['1.0','v1.0.0.0','1.0.0.0\nrun','1.0.0.70000']:
            with self.assertRaises(ValueError):catalog.version(value)
    def test_entry_uses_actual_zip_md5_and_release_url(self):
        with tempfile.TemporaryDirectory() as tmp:
            archive=pathlib.Path(tmp)/'JellyTube_0.1.0.0.zip';meta=json.loads((ROOT/'meta.json').read_text());meta['timestamp']='2026-11-07T00:00:00Z'
            with zipfile.ZipFile(archive,'w') as z:
                z.writestr('meta.json',json.dumps(meta));z.writestr('thumb.png',b'png')
                for name in meta['assemblies']:z.writestr(name,b'managed-test-fixture')
            record=catalog.entry(archive,'ThuGie/JellyTube','0.1.0.0')
            self.assertEqual(hashlib.md5(archive.read_bytes(),usedforsecurity=False).hexdigest(),record['versions'][0]['checksum'])
            self.assertEqual('https://github.com/ThuGie/JellyTube/releases/download/v0.1.0.0/JellyTube_0.1.0.0.zip',record['versions'][0]['sourceUrl'])
            with zipfile.ZipFile(archive,'a') as z:z.writestr('yt-dlp.exe',b'forbidden')
            with self.assertRaises(AssertionError):catalog.entry(archive,'ThuGie/JellyTube','0.1.0.0')
    def test_merge_preserves_history_and_rejects_mutating_published_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            base=pathlib.Path(tmp);(base/'src/Jellyfin.Plugin.JellyTube').mkdir(parents=True)
            (base/'meta.json').write_text((ROOT/'meta.json').read_text());(base/'build.yaml').write_text((ROOT/'build.yaml').read_text())
            (base/'src/Jellyfin.Plugin.JellyTube/Jellyfin.Plugin.JellyTube.csproj').write_text('<Project><Version>0.1.0.0</Version></Project>')
            def record(version,checksum):return {'guid':catalog.GUID,'versions':[{'version':version,'checksum':checksum,'timestamp':'2026-11-07T00:00:00Z','changelog':'Release'}]}
            previous=catalog.ROOT;catalog.ROOT=base
            try:
                catalog.merge(record('0.1.0.0','a'));catalog.merge(record('0.1.0.1','b'))
                self.assertEqual(['0.1.0.1','0.1.0.0'],[v['version'] for v in json.loads((base/'manifest.json').read_text())[0]['versions']])
                with self.assertRaises(ValueError):catalog.merge(record('0.1.0.0','different'))
                self.assertEqual('0.1.0.1',json.loads((base/'meta.json').read_text())['version'])
            finally:catalog.ROOT=previous
if __name__=='__main__':unittest.main()
