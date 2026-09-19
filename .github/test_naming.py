from pathlib import Path
import os, subprocess, tempfile, re
r=Path(__file__).resolve().parents[1]
s=(r/'Utils/FilmGrain_Universal_HEVC_AV1_StudioBridge.bat').read_text()
assert "$statusVersion.Text = 'v4.8.2 TEST N6Q9T'" in (r/'Utils/FilmGrain_Studio.ps1').read_text(encoding='utf-8-sig')
if os.name!='nt':raise SystemExit(0)
with tempfile.TemporaryDirectory(prefix='FGS_names_') as td:
 p=Path(td)/'中文 & (names)';p.mkdir()
 helper=s[s.index('\n:RESOLVE_PIXEL_GRAIN_NAME\n'):s.index('\n:PROCESS_HEVC_FILE\n')]
 env=os.environ.copy();env.update(INDIR=str(p)+os.sep,NAME='Nature & (test)',EXT='mp4',BITRATE_NUM='6000',UPLOAD_BITRATE_NUM='18000',SPEED_SUFFIX='UHQ',FPS_SUFFIX='_24p',DEINT_FILE_SUFFIX='',FRAME_SUFFIX='',LUT_FILE_SUFFIX='_LUT_Test_75',HDR_FILE_SUFFIX='',SUB_FILE_SUFFIX='',X264_FILE_SUFFIX='_SLOW_3PASS',X264_HIGH10='0',GRAIN_FILE_TAG='Classic35',GRAIN_MODE='PROCEDURAL',HEVC_SUFFIX='_FG_DG68_HEVC',FGSIM_CQ='')
 for mode in ['HEVC','X264','AV1']:
  block=s[s.index('\n:PROCESS_'+mode+'_FILE\n'):]
  if mode=='AV1':start=block.index('set "OUTPUT=%INDIR%')
  else:start=block.index('call :RESOLVE_PIXEL_GRAIN_NAME')
  end=block.index('\n\n',start)
  code=block[start:end]
  script='@echo off\nsetlocal DisableDelayedExpansion\n'+code+'\n>"%RESULT%" set OUTPUT\nexit /b 0\n'+helper
  (p/'names.cmd').write_bytes(script.replace('\n','\r\n').encode('ascii'))
  env.update(MODE=mode,RESULT=str(p/'result.txt'))
  subprocess.run(['cmd','/d','/c','chcp 65001>nul & names.cmd'],cwd=p,env=env,check=True,capture_output=True)
  name=next(x[7:] for x in (p/'result.txt').read_text(encoding='utf-8').splitlines() if x.startswith('OUTPUT='))
  expected={'HEVC':'Nature & (test)_HEVC_UHQ_6000k_24p_FG_DG68_LUT_Test_75.mp4','X264':'Nature & (test)_X264_SLOW_3PASS_6000k_24p_FG_DG68_LUT_Test_75.mp4','AV1':'Nature & (test)_AV1_UHQ_6000k_24p_GS_Classic35_LUT_Test_75.mp4'}[mode]
  assert Path(name).name==expected,(mode,name,expected)
 cases=[
 ('Nature_AV1_UHQ_6000k_24p_GS_Classic35','Nature_AV1_UHQ_6000k_24p_GS_Super8_REPLACED'),
 ('Nature_AV1_UHQ_6000k_24p_GS_Classic35_REPLACED','Nature_AV1_UHQ_6000k_24p_GS_Super8_REPLACED'),
 ('Nature_AV1_UHQ_6000k_24p_GS_TABLE_Custom_Table_LUT_Test_75_SDR_BT2390_SUB_REPLACED','Nature_AV1_UHQ_6000k_24p_GS_Super8_LUT_Test_75_SDR_BT2390_SUB_REPLACED'),
 ('Nature_AV1GS_Classic35_UHQ_6000k_24p','Nature_AV1GS_Super8_UHQ_6000k_24p_REPLACED'),
 ('Nature_AV1GS_Classic35_UHQ_6000k_24p_REPLACED','Nature_AV1GS_Super8_UHQ_6000k_24p_REPLACED'),
 ('Nature_AV1FG_Classic35_ADDED_AV1FG_16mm_REPLACED','Nature_AV1FG_Super8_REPLACED'),
 ('Nature & (test)','Nature & (test)_AV1FG_Super8_REPLACED')]
 for i,(source,expected) in enumerate(cases):
  d=p/str(i);d.mkdir();src=d/(source+'.mp4');src.write_bytes(b'source');raw=d/'raw.mp4';raw.write_bytes(b'output')
  ev=os.environ.copy();ev.update(FG_AV1_FINAL_INPUT=str(src),FG_AV1_FINAL_RAW_OUTPUT=str(raw),FG_AV1_FINAL_TAG='Super8',FG_AV1_FINAL_EXT='mp4',FG_AV1_FINAL_RESULT=str(d/'result.txt'),FG_AV1_SOURCE_GRAIN_ACTION='REPLACED',FG_STUDIO_MODE='1')
  cmd=['powershell.exe','-NoProfile','-File',str(r/'Utils/FilmGrain_AV1_FinalizeName.ps1')]
  proc=subprocess.run(cmd,env=ev,capture_output=True);assert proc.returncode==0,proc.stderr
  target=d/(expected+'.mp4');assert target.read_bytes()==b'output';assert src.read_bytes()==b'source';assert not raw.exists()
  raw.write_bytes(b'new-output');proc=subprocess.run(cmd,env=ev,capture_output=True);assert proc.returncode!=0;assert target.read_bytes()==b'output';assert raw.read_bytes()==b'new-output'
print('Naming: 3 CMD main routes; 7 AV1 legacy/current/repeated replacements and 7 collision guards passed.')
