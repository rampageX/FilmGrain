"""Run extracted Bridge control flow under real Windows CMD with encoder stubs."""
from pathlib import Path
import os, re, subprocess, tempfile
root=Path(__file__).resolve().parents[1]
bridge=root/'Utils/FilmGrain_Universal_HEVC_AV1_StudioBridge.bat'
s=bridge.read_text(encoding='ascii')
base=subprocess.check_output(['git','show','v4.8.2:Utils/FilmGrain_Universal_HEVC_AV1_StudioBridge.bat'],cwd=root).decode('ascii')
commands=lambda t:[x for x in t.splitlines() if x.startswith(('"%FFMPEG%"','"%OPEN_SVP_VSPIPE%"'))]
assert [x for x in commands(s) if ' -pass 3 ' not in x]==commands(base), 'Existing encoder commands changed'
firsts=[x for x in commands(s) if ' -pass 1 ' in x and 'libx264' in x]
thirds=[x for x in commands(s) if ' -pass 3 ' in x and 'libx264' in x]
assert len(thirds)==9
assert [x.replace(' -pass 3 ',' -pass 1 ') for x in thirds]==firsts
assert not bridge.read_bytes().startswith(b'\xef\xbb\xbf')
assert not re.search(rb'(?<!\r)\n',bridge.read_bytes())
assert not re.search(r'\^[ \t]+$',s,re.M)
assert '$script:X264RateMode = \'VBR1\'' in (root/'Utils/FilmGrain_Studio.ps1').read_text(encoding='utf-8-sig')
assert '![](images/Film_Grain_Studio.jpg)' in (root/'README.md').read_text(encoding='utf-8-sig')
print('Static checks: existing FFmpeg commands unchanged; all 9 pass-3 commands match pass 1 except pass number.')
if os.name!='nt': raise SystemExit(0)
lines=s.splitlines()
starts=[i for i,l in enumerate(lines) if re.match(r'if /i "%X264_PASS_MODE%"=="2PASS" goto ',l)]
assert len(starts)==9
cases=0
with tempfile.TemporaryDirectory(prefix='FGS_3pass_') as td:
 work=Path(td)/'中文 & (3pass)'; work.mkdir()
 stub='@echo off\n>>"%TRACE%" echo %1\nif "%1"=="%FAIL_PASS%" exit /b 7\nexit /b 0\n'
 (work/'encoder.cmd').write_bytes(stub.replace('\n','\r\n').encode('ascii'))
 for start in starts:
  end=next(i for i in range(start,len(lines)) if ' -pass 2 ' in lines[i] and 'libx264' in lines[i])
  segment=lines[start:end+2] # includes final encoder return-code capture
  rewritten=[]
  for line in segment:
   if line.startswith(('"%FFMPEG%"','"%OPEN_SVP_VSPIPE%"')):
    m=re.search(r' -pass (\d) ',line); n=m[1] if m else 'single'
    line='call encoder.cmd '+n
   rewritten.append(line)
  present={l[1:].strip().upper() for l in rewritten if l.startswith(':')}
  targets=set(re.findall(r'\bgoto\s+([A-Z0-9_]+)', '\n'.join(rewritten),re.I))
  trailer=['goto TEST_END']
  for target in sorted(targets):
   if target.upper() in present: continue
   trailer += [':'+target]
   if target=='X264_MAIN_FAIL_PASS1': trailer+=['set "X264_MAIN_RC=%X264_PASS1_RC%"']
   trailer+=['goto TEST_END']
  trailer += [':CLEAN_X264_PASSLOG','exit /b 0',':CLEAN_UPLOAD_SUBTITLE','exit /b 0',':TEST_END','if defined X264_MAIN_RC exit /b %X264_MAIN_RC%','if defined UPLOAD_RUN_RC exit /b %UPLOAD_RUN_RC%','exit /b 0']
  text='\n'.join(['@echo off','setlocal DisableDelayedExpansion']+rewritten+trailer)+'\n'
  (work/'route.cmd').write_bytes(text.replace('\n','\r\n').encode('ascii'))
  for mode,fail,expected in [('VBR1','',['single']),('2PASS','',['1','2']),('3PASS','',['1','3','2']),('3PASS','1',['1']),('3PASS','3',['1','3']),('3PASS','2',['1','3','2'])]:
   trace=work/'trace.txt'; trace.unlink(missing_ok=True)
   env=os.environ.copy(); env.update(X264_PASS_MODE=mode,FAIL_PASS=fail,TRACE=str(trace),INDIR=str(work)+os.sep)
   r=subprocess.run(['cmd.exe','/d','/c','route.cmd'],cwd=work,env=env,capture_output=True)
   actual=trace.read_text().splitlines()
   assert actual==expected,(lines[start],mode,fail,actual,expected,r.stdout,r.stderr)
   assert (r.returncode!=0)==bool(fail),(lines[start],mode,fail,r.returncode,r.stdout,r.stderr)
   cases+=1
 # Real CMD naming/settings routine, including all presets and default behavior.
 routine=s[s.index('\n:RESOLVE_X264_SETTINGS\n')+1:s.index('\n:SELECT_FRAMING\n')+1]
 for preset in ['faster','medium','slow']:
  for mode in ['VBR1','2PASS','3PASS']:
   script='@echo off\nsetlocal DisableDelayedExpansion\ncall :RESOLVE_X264_SETTINGS\necho RESULT=%X264_FILE_SUFFIX%\nexit /b 0\n'+routine
   (work/'settings.cmd').write_bytes(script.replace('\n','\r\n').encode('ascii'))
   env=os.environ.copy(); env.update(X264_PRESET=preset,X264_PASS_MODE=mode)
   r=subprocess.check_output(['cmd.exe','/d','/c','settings.cmd'],cwd=work,env=env).decode()
   expected=('' if preset=='faster' else '_'+preset.upper())+('' if mode=='VBR1' else '_'+mode)
   assert 'RESULT='+expected+'\r\n' in r,(preset,mode,r)
print(f'Windows CMD: {cases} route/order/failure cases and 9 preset/naming cases passed.')
