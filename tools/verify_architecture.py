"""检查项目依赖方向、循环及后台库的传递桌面依赖；不运行应用或连接设备。

运行：python tools/verify_architecture.py [--report 输出.json]
"""
from pathlib import Path
import argparse, json, sys, xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--report', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
projects = {}
errors = []
for area in ('Framework', 'Hardware', 'Applications', 'Company.Shell'):
    for path in (root / area).rglob('*.csproj'):
        if any(p in ('obj', 'bin') for p in path.relative_to(root).parts): continue
        xml = ET.parse(path).getroot()
        projects[path.resolve()] = dict(
            name=path.stem, path=path.relative_to(root).as_posix(),
            references=[(path.parent / e.attrib['Include'].replace('\\','/')).resolve() for e in xml.iter('ProjectReference')],
            packages=[e.attrib['Include'] for e in xml.iter('PackageReference')],
            wpf=any(e.text == 'true' for e in xml.iter('UseWPF')),
            desktop=any('WindowsDesktop' in e.get('Include','') for e in xml.iter('FrameworkReference')))
backend = {'Company.Core','Company.Logger','Company.Database','Company.Database.SqlSugar','Company.Platform.Services',
           'Company.Platform.Shared','Company.PlcHmi.Shared','Company.PlcHmi.Plc','Company.PlcHmi.Persistence',
           'Company.Hmi.Contracts','Company.FlyScan.Runtime'}
for p in projects.values():
    if p['path'].startswith('Hardware/'): backend.add(p['name'])

visiting, visited = set(), set()
def visit(path):
    if path in visiting:
        errors.append('循环引用：' + projects[path]['name']); return
    if path in visited: return
    visiting.add(path)
    for ref in projects[path]['references']:
        if ref not in projects:
            errors.append('未登记的引用：' + projects[path]['name'] + ' -> ' + str(ref)); continue
        visit(ref)
    visiting.remove(path)
    visited.add(path)
for path in projects: visit(path)

def closure(path, seen=None):
    seen = set() if seen is None else seen
    if path in seen: return seen
    seen.add(path)
    for ref in projects[path]['references']:
        if ref in projects: closure(ref, seen)
    return seen

for path, p in projects.items():
    for ref in p['references']:
        if ref not in projects: continue
        q = projects[ref]
        if p['path'].startswith('Framework/') and not q['path'].startswith('Framework/'):
            errors.append(f"公共框架反向依赖：{p['name']} -> {q['name']}")
        if p['path'].startswith('Hardware/') and q['path'].startswith(('Applications/', 'Company.Shell/')):
            errors.append(f"硬件反向依赖应用：{p['name']} -> {q['name']}")
        if p['name'] in ('Company.PlcHmi.Shared','Company.Platform.Shared') and q['path'].startswith('Applications/'):
            errors.append(f"共享契约依赖应用实现：{p['name']} -> {q['name']}")
    application = p['path'].split('/')[:2]
    if application[0] == 'Applications':
        for dep in closure(path) - {path}:
            q = projects[dep]
            if q['path'].startswith('Company.Shell/'):
                errors.append(f"应用反向依赖入口：{p['name']} -> {q['name']}")
            dependency_application = q['path'].split('/')[:2]
            if dependency_application[0] == 'Applications' and dependency_application != application:
                errors.append(f"产品跨应用依赖：{p['name']} -> {q['name']}")
    if p['name'] in backend:
        for dep in closure(path):
            q = projects[dep]
            forbidden = [x for x in q['packages'] if x.startswith(('Prism.Wpf','Prism.DryIoc','ReactiveUI','MahApps.'))]
            if q['wpf'] or q['desktop'] or q['name'] in ('Company.Desktop','Company.UI','Company.Hmi.Presentation') or forbidden:
                errors.append(f"后台库传递依赖桌面：{p['name']} -> {q['name']}")

report = dict(passed=not errors, projectCount=len(projects), headlessProjects=sorted(backend), errors=errors,
              projects=[dict(name=p['name'],path=p['path'],references=[projects[r]['name'] for r in p['references'] if r in projects]) for p in projects.values()])
if args.report:
    args.report.parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(f"Architecture: {len(projects)} projects, {len(backend)} headless libraries, {len(errors)} violations.")
for error in errors: print(error, file=sys.stderr)
sys.exit(0 if report['passed'] else 1)
