"""Owner-operated Messaging-only deployment. No SQL, migration, provider or credential writes."""
import base64,hashlib,json,os,re,stat,subprocess,sys,tempfile,time,urllib.request,urllib.error,zipfile
from pathlib import Path

UNIT='xanhnow-messaging-api.service'
COMMIT='8e0e298d1346d6da7172c18d8f353821e9150954'
NODES=('192.168.2.25','192.168.2.38','192.168.2.65')
DROPIN=Path('/etc/systemd/system')/(UNIT+'.d')/'zzzzzz-s101-messaging-fk-order.conf'
MUTATIONS=[]
def require(ok,reason):
    if not ok:raise ValueError(reason)
def run(*args):return subprocess.run(args,capture_output=True,text=True,timeout=60)
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def show(unit,*fields):
    result=run('systemctl','show',unit,*[arg for field in fields for arg in ('-p',field)])
    require(result.returncode==0,'service_metadata_unconfirmed')
    return dict(x.split('=',1) for x in result.stdout.splitlines() if '=' in x)
def trusted(path):
    require(path.is_dir() and not path.is_symlink() and path.stat().st_uid==0
        and not stat.S_IMODE(path.stat().st_mode)&0o022,'trusted_directory_required')
def protected():
    result=run('systemctl','list-units','--type=service','--all','--no-legend','--plain')
    require(result.returncode==0,'protected_inventory_unconfirmed')
    units=sorted({line.split()[0] for line in result.stdout.splitlines() if line.split()
       and re.search(r'xanhnow|vault|passkey|smart.?otp|auth.?login|jwt|device.?binding',line.split()[0],re.I)}-{UNIT})
    require({'xanhnow-security-api.service','xanhnow-membership-api.service','xanhnow-membership-worker.service'}<=set(units),'protected_services_missing')
    result=run('systemctl','show',*units,'-p','Id','-p','MainPID','-p','InvocationID','-p','ActiveState')
    require(result.returncode==0,'protected_snapshot_unconfirmed')
    return result.stdout
def validate(m):
    require(m['source_commit']==COMMIT and m['unit']==UNIT and re.fullmatch(r'messaging-fk-[0-9TZ]+',m['release']),'manifest_scope_mismatch')
    require(m['entry']=='XanhNow.Messaging.Api.dll' and isinstance(m['files'],dict) and 5<=len(m['files'])<=150,'invalid_payload_inventory')
    require({'XanhNow.Messaging.Api.dll','XanhNow.Messaging.Infrastructure.dll','XanhNow.Messaging.Contracts.dll',
        'XanhNow.Messaging.Api.deps.json','XanhNow.Messaging.Api.runtimeconfig.json'}<=set(m['files']),'incomplete_payload')
    for name,digest in m['files'].items():
        p=Path(name)
        require(re.fullmatch(r'[A-Za-z0-9_.+/-]+',name) and not p.is_absolute() and '..' not in p.parts
            and not p.name.lower().startswith('appsettings') and 'migrator' not in name.lower()
            and re.fullmatch(r'[a-f0-9]{64}',digest),'unapproved_payload_file')
def inspect(node):
    require(os.geteuid()==0 and node in NODES and node in run('hostname','-I').stdout.split(),'sudo_or_node_mismatch')
    info=show(UNIT,'MainPID','ActiveState','DropInPaths','FragmentPath')
    pid=info.get('MainPID','')
    require(info.get('ActiveState')=='active' and pid.isdigit() and int(pid)>1,'messaging_not_active')
    proc=Path('/proc')/pid
    args=[s.decode() for s in (proc/'cmdline').read_bytes().split(b'\0') if s]
    entry=[i for i,x in enumerate(args) if x.endswith('/XanhNow.Messaging.Api.dll')]
    require(len(entry)==1 and Path(args[entry[0]]).is_absolute() and args[0] in ('/usr/bin/dotnet','/usr/share/dotnet/dotnet'),'messaging_entry_requires_review')
    i=entry[0];runtime=args[1:i];tail=args[i+1:]
    require(runtime==[] or (len(runtime)==2 and runtime[0]=='--roll-forward' and runtime[1] in ('Major','LatestMajor','Minor','LatestMinor','LatestPatch','Disable')),'runtime_arguments_require_review')
    require(tail in ([],['--urls','http://127.0.0.1:5052'],['--urls=http://127.0.0.1:5052']),'app_arguments_require_review')
    cwd=(proc/'cwd').resolve(strict=True)
    env=dict(s.decode().split('=',1) for s in (proc/'environ').read_bytes().split(b'\0') if b'=' in s)
    require(env.get('DOTNET_ENVIRONMENT',env.get('ASPNETCORE_ENVIRONMENT','Production'))=='Production','production_required')
    require(not env.get('ASPNETCORE_CONTENTROOT') or Path(env['ASPNETCORE_CONTENTROOT']).resolve()==cwd,'content_root_requires_review')
    config={};config_files={}
    def flatten(obj,prefix=''):
        for k,v in obj.items():
            key=(prefix+':'+k if prefix else k).lower()
            if isinstance(v,dict):flatten(v,key)
            else:config[key]=v
    for name in ('appsettings.json','appsettings.Production.json'):
        path=cwd/name
        if path.is_file():
            require(path.stat().st_size<=1048576,'configuration_too_large')
            flatten(json.loads(path.read_text(encoding='utf-8-sig')));config_files[str(path)]=sha(path)
    config.update({k.lower().replace('__',':'):v for k,v in env.items()})
    files={}
    keys=('database:connectionstringfile','securityboundary:serviceapikeyfile','securityboundary:devicetokenencryptionkeyfile',
          'realtime:redisconfigurationfile','realtime:redispasswordfile','kafka:passwordfile','kafka:cafile')
    for key in keys:
        value=config.get(key,'');require(isinstance(value,str) and value,'existing_reference_missing')
        path=Path(value);require(path.is_absolute() and path.is_file() and 0<path.stat().st_size<=1048576,'existing_reference_unconfirmed')
        files[key]=(str(path),sha(path))
    unit_files={}
    for name in [info.get('FragmentPath',''),*info.get('DropInPaths','').split()]:
        if name==str(DROPIN):continue
        path=Path(name);require(path.is_absolute() and path.is_file(),'unit_file_unconfirmed')
        require(path.name<=DROPIN.name or name==info.get('FragmentPath'),'later_dropin_requires_review')
        unit_files[str(path)]=sha(path)
    selected={k:v for k,v in env.items() if k not in ('INVOCATION_ID','JOURNAL_STREAM','SYSTEMD_EXEC_PID','LISTEN_PID')}
    return {'pid':pid,'entry':args[i],'cwd':str(cwd),'runtime':runtime,'tail':tail,
        'environment_digest':hashlib.sha256(json.dumps(selected,sort_keys=True).encode()).hexdigest(),
        'reference_files':files,'config_files':config_files,'unit_files':unit_files}
def health():
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self,*args):return None
    opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),NoRedirect())
    for path in ('/health/live','/health/ready'):
        deadline=time.monotonic()+30
        while True:
            try:
                with opener.open('http://127.0.0.1:5052'+path,timeout=3) as r:
                    if r.status==200:break
            except (OSError,urllib.error.HTTPError):pass
            require(time.monotonic()<deadline,'messaging_health_unconfirmed');time.sleep(1)
def private_json(path,data):
    with path.open('x') as out:
        os.fchmod(out.fileno(),0o600);json.dump(data,out);out.flush();os.fsync(out.fileno())
def apply(root,m,node):
    old=inspect(node);before=protected()
    require(not DROPIN.exists() and not DROPIN.is_symlink(),'prior_deployment_dropin_exists')
    require(sha(root/'messaging-api.zip')==m['archive_sha256'],'uploaded_archive_hash_mismatch')
    dest=Path('/opt/s101-xanhnow-messaging/fk-order')/m['release']/'api'
    require(not dest.parent.exists(),'release_already_staged')
    dest.mkdir(parents=True,mode=0o755)
    for parent in [dest,*list(dest.parents)[:-1]]:trusted(parent)
    with zipfile.ZipFile(root/'messaging-api.zip') as archive:
        require(len(archive.namelist())==len(m['files']) and set(archive.namelist())==set(m['files']),'archive_inventory_mismatch')
        for name,digest in m['files'].items():
            data=archive.read(name);require(hashlib.sha256(data).hexdigest()==digest,'candidate_hash_mismatch')
            target=dest/name;require(target.resolve().is_relative_to(dest.resolve()),'unsafe_payload_path')
            target.parent.mkdir(parents=True,exist_ok=True)
            with target.open('xb') as out:out.write(data)
            target.chmod(0o644)
    private_json(dest.parent/'before-state.json',{'node':node,'old':old,'protected':before,'credentials_stored':False})
    require(inspect(node)==old and protected()==before,'baseline_changed_before_activation')
    DROPIN.parent.mkdir(exist_ok=True,parents=True,mode=0o755);trusted(DROPIN.parent)
    args=['/usr/bin/dotnet',*old['runtime'],str(dest/m['entry']),*old['tail']]
    require(all(not re.search(r'[%\r\n"\\]',x) for x in args),'unsafe_execstart_argument')
    content='[Service]\nExecStart=\nExecStart='+' '.join(args)+'\n'
    fd,tmp=tempfile.mkstemp(prefix='.messaging-fk-',dir=DROPIN.parent)
    try:
        with os.fdopen(fd,'w') as out:
            os.fchmod(out.fileno(),0o644);out.write(content);out.flush();os.fsync(out.fileno())
        require(not DROPIN.exists() and not DROPIN.is_symlink(),'dropin_appeared_during_activation')
        os.link(tmp,DROPIN);MUTATIONS.append('messaging_execstart_dropin')
    finally:
        if Path(tmp).exists():Path(tmp).unlink()
    require(run('systemctl','daemon-reload').returncode==0,'systemd_reload_unconfirmed')
    MUTATIONS.append('messaging_restart_requested')
    require(run('systemctl','restart',UNIT).returncode==0,'messaging_restart_unconfirmed')
    current=inspect(node)
    require(current['entry']==str(dest/m['entry']) and current['pid']!=old['pid'],'new_messaging_entry_unconfirmed')
    for key in ('cwd','environment_digest','reference_files','config_files','unit_files','runtime','tail'):
        require(current[key]==old[key],'preserved_configuration_or_credential_changed')
    require(all(sha(dest/name)==digest for name,digest in m['files'].items()),'active_payload_mismatch')
    health()
    require(protected()==before,'protected_process_changed')
    private_json(dest.parent/'activation-confirmed.json',{'node':node,'release':m['release'],'confirmed':True,'only_messaging_restart_requested':True})
    print(json.dumps({'status':'MESSAGING_FK_ORDER_FIX_ACTIVE','node':node,'only_messaging_restart_requested':True,
        'config_and_credential_fingerprints_preserved':True,'login_membership_worker_children_vault_agents_unchanged':True,
        'health_live_and_ready_verified':True,'migration_executed':False,'manual_sql_or_callback_sent':False,
        'queued_events_may_resume_normally':True,'phone_notification_verified':False,'secret_values_printed':False}),flush=True)
def main():
    mode,node=sys.argv[1:3]
    if mode=='Check':
        m=json.loads(base64.b64decode(sys.argv[3]));validate(m)
        require(not DROPIN.exists() and not DROPIN.is_symlink(),'prior_deployment_dropin_exists')
        inspect(node);protected();health()
        print(json.dumps({'status':'MESSAGING_FK_PREFLIGHT_PASS','node':node,'changes':False,'restart_requested':False}),flush=True)
    elif mode=='Apply':
        root=Path(sys.argv[3]).resolve(strict=True)
        require(sha(root/'release.json')==sys.argv[4],'uploaded_manifest_hash_mismatch')
        m=json.loads((root/'release.json').read_text());validate(m);apply(root,m,node)
    else:raise ValueError('invalid_mode')
if __name__=='__main__':
    try:main()
    except Exception as error:
        reason=str(error) if isinstance(error,ValueError) and re.fullmatch(r'[a-z_]+',str(error)) else 'operation_unconfirmed'
        print(json.dumps({'status':'STOP','reason':reason,'partial_mutations':MUTATIONS,
            'automatic_retry_or_rollback':False,'raw_error_or_secret_values_printed':False}),flush=True)
        sys.exit(2)
