import {readFileSync,writeFileSync,readdirSync,copyFileSync,renameSync,existsSync,createReadStream} from 'node:fs';
import {resolve,join,dirname,basename} from 'node:path';
import {createHash} from 'node:crypto';
const root=resolve(import.meta.dirname,'..'),base=join(root,'.omx/verification'),exe=join(root,'dist/FreeCamRecord.exe');
const hash=createHash('sha256');for await(const chunk of createReadStream(exe))hash.update(chunk);const expectedHash=hash.digest('hex').toUpperCase();
const json=file=>JSON.parse(readFileSync(file,'utf8').replace(/^\uFEFF/,''));
const rows=[];
for(const name of readdirSync(base).filter(n=>n.startsWith('final-matrix-'))){
 const directory=join(base,name),record=json(join(directory,'recording.json')),motion=json(join(directory,'motion-analysis.json')),av=json(join(directory,'av-analysis.json')),player=json(join(directory,'chrome-playback-clean/report.json'));
 const trace=readFileSync(join(directory,'ui-trace.txt'),'utf8');
 const assert=(condition,message)=>{if(!condition)throw Error(name+': '+message);};
 assert(resolve(record.Executable).toLowerCase()===exe.toLowerCase(),'wrong executable path');
 assert(record.Hash===expectedHash&&record.Version==='0.3.1.0','wrong packaged version or SHA256');
 assert(record.SystemAudio&&record.BrowserExecutable.toLowerCase().endsWith('chrome.exe'),'audio/browser mismatch');
 assert(trace.includes('Physical drag/resize')&&trace.includes('Actual start button clicked')&&trace.includes('Actual stop button clicked'),'actual GUI evidence missing');
 assert(motion.frameCounterAdvanced&&motion.duplicateFramePercent<2&&motion.longestRepeatedFrameMs<100,'recorded content freezes');
 assert(av.pass&&av.maxAbsOffsetMs<150,'audio/flash timing failed');
 assert(player.status==='completed'&&player.playback.waitingMs<100&&player.playback.corruptedVideoFrames===0&&player.playback.droppedPercent<1,'Chrome playback failure');
 assert(player.playback.steadyDisplayIntervalMs.max<100,'long Chrome presentation gap');
 const savedName=basename(record.OriginalSavedPath||record.Path),stamp=/錄影_(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})_(\d{3})_/.exec(savedName);
 assert(stamp,'missing original application-generated recording timestamp');
 const started=new Date(+stamp[1],+stamp[2]-1,+stamp[3],+stamp[4],+stamp[5],+stamp[6],+stamp[7]).getTime();
 const samples=json(join(directory,'browser-samples.json')).filter(s=>s.wallTime>started+1500&&s.wallTime<started+record.Seconds*1000-500);
 assert(samples.length>=3&&samples.every(s=>s.focused&&s.visibility==='visible'&&s.audio==='running'),'Chrome lost focus or audio during recording');
 const mediaName=`final-ui-${record.Fps}fps.mp4`;
 copyFileSync(record.Path,join(root,'dist',mediaName));
 rows.push({fps:record.Fps,version:record.Version,hash:record.Hash,chrome:record.BrowserVersion,audio:true,focusedSourceSamples:samples.length,distinctFps:motion.distinctFramesPerSecond,repeats:motion.duplicateFramePercent,maxHoldMs:motion.longestRepeatedFrameMs,chromeDropped:player.playback.droppedPercent,chromeSteadyMaxGapMs:player.playback.steadyDisplayIntervalMs.max,audioOffsetMs:av.maxAbsOffsetMs,file:mediaName,evidence:name});
 // Archive only clips created by these known test sessions. Never touch the user's supplied recording.
 const archive=join(directory,'recorded.mp4');
 if(resolve(dirname(record.Path)).toLowerCase()===join(process.env.USERPROFILE,'Videos/FreeCamRecord').toLowerCase()){
  assert(!existsSync(archive),'archive already exists');
  const original=record.Path;renameSync(original,archive);record.OriginalSavedPath=original;record.Path=archive;
  writeFileSync(join(directory,'recording.json'),JSON.stringify(record,null,2));
 }
}
rows.sort((a,b)=>a.fps-b.fps);
if(rows.map(r=>r.fps).join(',')!=='30,60,120,165')throw Error('Incomplete FPS matrix');
writeFileSync(join(base,'final-acceptance.json'),JSON.stringify({pass:true,executable:exe,sha256:expectedHash,rows,scope:'Actual final GUI in ordinary Chrome, focus-sensitive test scene, system audio on, Chrome playback. Original unidentified game not independently verified.'},null,2));
const html=`<!doctype html><html lang="zh-Hant"><meta charset="utf-8"><title>FreeCamRecord Chrome 驗收</title><style>body{margin:0;background:#111c2c;color:#edf3fa;font:15px system-ui;padding:20px}h1{font-size:20px}button{padding:9px 16px;margin:0 8px 12px 0;background:#253b55;color:white;border:1px solid #5aa1d3;border-radius:6px;cursor:pointer}video{display:block;width:100%;max-height:75vh;background:black}p{color:#b8cadf}#detail{font-variant-numeric:tabular-nums}</style><h1>FreeCamRecord 0.3.1 · 最終 EXE 實際操作驗收</h1><p>普通 Chrome、實際拖框與按鈕操作、系統音效開啟。測試頁失去焦點會暫停，用來驗證錄影工具列不會讓來源停住。選擇 FPS，再按影片播放。</p><div id="choices"></div><div id="detail"></div><video id="video" controls playsinline></video><p>片內來源 FPS 是從網頁啟動起算的平均值，包含錄影前操作選單的時間；下列結果使用影片逐格編號及實際 Chrome 回放測量。120 FPS 的瀏覽器回呼節奏不保證每幀都回報，未宣稱所有播放器都能完美呈現120FPS。</p><script>const rows=${JSON.stringify(rows)},video=document.querySelector('video');for(const r of rows){let b=document.createElement('button');b.textContent=r.fps+' FPS';b.onclick=()=>{video.src=r.file;document.querySelector('#detail').textContent='影格重複 '+r.repeats.toFixed(2)+'% · 最長畫面維持 '+r.maxHoldMs.toFixed(2)+' ms · Chrome掉幀 '+r.chromeDropped.toFixed(2)+'%';};document.querySelector('#choices').append(b);}document.querySelector('button').click();</script></html>`;
writeFileSync(join(root,'dist/Chrome-FPS-Test.html'),html);
console.log(JSON.stringify({pass:true,sha256:expectedHash,rows},null,2));
