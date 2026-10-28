"""Offline sealed-package and deployment-scope checks. No network or service calls."""
import ast,base64,copy,gzip,hashlib,importlib.util,json,zipfile
from pathlib import Path
base=Path(__file__).parent
latest=json.loads((base/'latest-messaging-fk.json').read_text())
root=base/latest['candidate']
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
assert sha(root/'release.json')==latest['manifest_sha256']
m=json.loads((root/'release.json').read_text())
assert sha(root/'messaging-api.zip')==m['archive_sha256'] and sha(base/'messaging-fk-node.py')==m['node_sha256']
spec=importlib.util.spec_from_file_location('deployment',base/'messaging-fk-node.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
module.validate(m)
assert m['unit']=='xanhnow-messaging-api.service' and m['source_commit']==module.COMMIT
assert not m['migrator_in_archive'] and not m['configuration_files_in_archive']
with zipfile.ZipFile(root/'messaging-api.zip') as z:
    assert set(z.namelist())==set(m['files']) and len(z.namelist())==len(m['files'])
    assert all(hashlib.sha256(z.read(name)).hexdigest()==digest for name,digest in m['files'].items())
    for name in ('XanhNow.Messaging.Api.dll','XanhNow.Messaging.Infrastructure.dll','XanhNow.Messaging.Contracts.dll'):
        assert module.COMMIT.encode() in z.read(name)
for change in ('unit','file','commit'):
    bad=copy.deepcopy(m)
    if change=='unit':bad['unit']='xanhnow-security-api.service'
    elif change=='commit':bad['source_commit']='0'*40
    else:bad['files']['appsettings.Production.json']='0'*64
    try:module.validate(bad)
    except ValueError:pass
    else:raise AssertionError('Unapproved package accepted')
size=len(base64.b64encode(gzip.compress((base/'messaging-fk-node.py').read_bytes())))+len(base64.b64encode((root/'release.json').read_bytes()))+600
assert size<27000
tree=ast.parse((base/'messaging-fk-node.py').read_text())
restarts=[]
for call in ast.walk(tree):
    if isinstance(call,ast.Call) and isinstance(call.func,ast.Name) and call.func.id=='run' and len(call.args)>=2:
        if isinstance(call.args[0],ast.Constant) and call.args[0].value=='systemctl' and isinstance(call.args[1],ast.Constant):
            verb=call.args[1].value
            assert verb not in ('stop','start','reload','reload-or-restart','enable')
            if verb=='restart':restarts.append(call)
assert len(restarts)==1 and isinstance(restarts[0].args[2],ast.Name) and restarts[0].args[2].id=='UNIT'
ps=(base/'deploy-messaging-fk.ps1').read_text()
assert ps.index('Check $node')<ps.index('if(-not $Apply)')<ps.index('foreach($row in $receipt.nodes)')
assert '[IO.FileMode]::CreateNew' in ps and '$row.activationAttempted=$true' in ps
assert all(node in ps for node in ('192.168.2.25','192.168.2.38','192.168.2.65'))
# Fail the baseline gate and verify activation cannot restart or extract anything.
calls=[]
module.inspect=lambda node: (_ for _ in ()).throw(ValueError('baseline_gate'))
module.run=lambda *args:calls.append(args)
try:module.apply(root,m,'192.168.2.38')
except ValueError as error:assert str(error)=='baseline_gate'
else:raise AssertionError('Baseline gate bypassed')
assert calls==[]
print('PASS sealed package/revision, no config/migrator, only Messaging restart, all-node preflight, immutable receipt, failure gate and SSH size; no remote operations.')
