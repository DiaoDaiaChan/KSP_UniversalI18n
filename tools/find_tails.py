import re, sys
sys.path.insert(0, 'tools')
from i18n_tools.harvest_cfgcache import iter_fields

cache_path = 'D:/Kerbal Space Program_newmod/GameData/ModuleManager.ConfigCache'
stats = {'assign': {}}

tail_patterns = {}
for field, raw, mod in iter_fields(cache_path, ('description',), stats):
    # Find any english text after CJK text
    m = re.search(r'[一-鿿].*?([A-Za-z][A-Za-z0-9 .,/<>=_-]{8,})$', raw)
    if m:
        tail = m.group(1).strip()
        tail_clean = re.sub(r'<[^>]+>', '', tail).strip()
        if len(tail_clean) >= 8:
            tail_patterns[tail_clean] = tail_patterns.get(tail_clean, 0) + 1

print(f"Found {len(tail_patterns)} trailing English patterns:")
for t, count in sorted(tail_patterns.items(), key=lambda x: -x[1])[:30]:
    print(f"  [{count}x] {repr(t)}")
