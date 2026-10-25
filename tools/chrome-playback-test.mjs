import {createServer} from 'node:http';
import {createReadStream,statSync,mkdirSync,writeFileSync} from 'node:fs';
import {spawn} from 'node:child_process';
import {resolve,join} from 'node:path';

const [input, destination]=process.argv.slice(2);
if(!input||!destination)throw Error('Usage: node tools/chrome-playback-test.mjs <mp4Path> <outputDirectory>');
const clip=resolve(input),output=resolve(destination),root=resolve(import.meta.dirname,'..');
mkdirSync(output,{recursive:true});
const wait=ms=>new Promise(r=>setTimeout(r,ms));
const streams=new Set(),properties=[];let server,chrome,ws,call,report;
const html=`<!doctype html><meta charset="utf-8"><title>FreeCamRecord Chrome Playback QA</title>
<style>html,body{margin:0;background:#101a2a;color:#eff6ff;font:16px sans-serif}header{padding:12px}video{display:block;width:100%;height:calc(100vh - 48px);object-fit:contain}</style>
<header id="status">Chrome 播放驗證</header><video id="video" controls muted playsinline></video><script>
const video=document.querySelector('video'),status=document.querySelector('#status');
window.playbackTest=async()=>{
 const events=[],display=[],media=[],steadyDisplay=[],steadyMedia=[],samples=[],canvas=document.createElement('canvas');canvas.width=24;canvas.height=16;
 const sampleContent=${process.env.PLAYBACK_CONTENT_SAMPLES==='1'};
 const ctx=canvas.getContext('2d',{willReadFrequently:true});let callbacks=0,lastDisplay,lastMedia,lastSample=-1,lastStatus=-1,lastPixels,waitingStart=null,waitingMs=0;
 const started=performance.now();
 for(const type of ['waiting','stalled','playing','pause','error','ended'])video.addEventListener(type,()=>{
  events.push({type,wallMs:performance.now()-started,mediaTime:video.currentTime});
  if(type==='waiting'&&waitingStart===null)waitingStart=performance.now();
  if((type==='playing'||type==='ended')&&waitingStart!==null){waitingMs+=performance.now()-waitingStart;waitingStart=null;}
 });
 let stopped=false;
 const frame=(now,m)=>{
  callbacks++;if(lastDisplay!==undefined)display.push(m.expectedDisplayTime-lastDisplay);if(lastMedia!==undefined)media.push((m.mediaTime-lastMedia)*1000);
  if(lastMedia>=1){steadyDisplay.push(m.expectedDisplayTime-lastDisplay);steadyMedia.push((m.mediaTime-lastMedia)*1000);}
  lastDisplay=m.expectedDisplayTime;lastMedia=m.mediaTime;
  if(sampleContent&&m.mediaTime-lastSample>=.2){
   ctx.drawImage(video,0,0,24,16);const pixels=ctx.getImageData(0,0,24,16).data;let hash=2166136261,delta=0;
   for(let i=0;i<pixels.length;i++){if(i%4===3)continue;hash=Math.imul(hash^(pixels[i]>>3),16777619);if(lastPixels)delta+=Math.abs(pixels[i]-lastPixels[i]);}
   samples.push({mediaTime:m.mediaTime,hash:(hash>>>0).toString(16),meanDelta:lastPixels?delta/(24*16*3):null});lastPixels=new Uint8ClampedArray(pixels);lastSample=m.mediaTime;
  }
  if(m.mediaTime-lastStatus>=1){status.textContent='Chrome 播放中 '+video.currentTime.toFixed(2)+' / '+video.duration.toFixed(2)+' 秒';lastStatus=m.mediaTime;}
  if(!stopped)video.requestVideoFrameCallback(frame);
 };
 video.src='/video.mp4';video.load();
 await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('Video metadata timeout')),15000);video.onloadedmetadata=()=>{clearTimeout(timer);resolve();};video.onerror=()=>{clearTimeout(timer);reject(Error('Video error '+video.error?.message));};});
 if(!Number.isFinite(video.duration)||video.duration<=0)throw Error('Invalid video duration');
 video.requestVideoFrameCallback(frame);
 const ended=new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('Playback did not reach ended')),Math.min(600000,(video.duration+30)*1000));video.onended=()=>{clearTimeout(timer);resolve();};video.onerror=()=>{clearTimeout(timer);reject(Error('Video error '+video.error?.message));};});
 await video.play();await ended;stopped=true;
 const q=video.getVideoPlaybackQuality();
 const summary=a=>{const sorted=a.filter(Number.isFinite).sort((a,b)=>a-b);const p=n=>sorted[Math.min(sorted.length-1,Math.floor(sorted.length*n))]??null;return {count:a.length,p50:p(.5),p95:p(.95),p99:p(.99),max:sorted.at(-1)??null};};
 status.textContent='播放完成；播放器掉幀 '+q.droppedVideoFrames+' / '+q.totalVideoFrames;
 return {duration:video.duration,width:video.videoWidth,height:video.videoHeight,muted:video.muted,wallSeconds:(performance.now()-started)/1000,
 totalVideoFrames:q.totalVideoFrames,droppedVideoFrames:q.droppedVideoFrames,corruptedVideoFrames:q.corruptedVideoFrames,
 droppedPercent:q.totalVideoFrames?100*q.droppedVideoFrames/q.totalVideoFrames:0,requestVideoFrameCallbacks:callbacks,
 displayIntervalMs:summary(display),mediaIntervalMs:summary(media),steadyDisplayIntervalMs:summary(steadyDisplay),steadyMediaIntervalMs:summary(steadyMedia),waitingMs,events,contentSamplingEnabled:sampleContent,contentSamples:samples,
 caveat:'Player drops and recorded content freezes are different; zero player drops does not prove smooth recorded content. Pixel samples are only a 5Hz heuristic.'};
};</script>`;

try{
 const size=statSync(clip).size;
 server=createServer((req,res)=>{
  if(req.url==='/'){res.writeHead(200,{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store'});res.end(html);return;}
  if(req.url!=='/video.mp4'){res.writeHead(404);res.end();return;}
  let start=0,end=size-1;const range=req.headers.range;
  if(range){
   const match=/^bytes=(\d*)-(\d*)$/.exec(range);
   if(!match||(!match[1]&&!match[2])){res.writeHead(416,{'Content-Range':'bytes */'+size});res.end();return;}
   if(!match[1])start=Math.max(0,size-Number(match[2]));else {start=Number(match[1]);if(match[2])end=Math.min(end,Number(match[2]));}
   if(!Number.isSafeInteger(start)||!Number.isSafeInteger(end)||start<0||start>=size||end<start){res.writeHead(416,{'Content-Range':'bytes */'+size});res.end();return;}
  }
  const headers={'Content-Type':'video/mp4','Accept-Ranges':'bytes','Content-Length':end-start+1,'Cache-Control':'no-store'};
  if(range)headers['Content-Range']='bytes '+start+'-'+end+'/'+size;
  res.writeHead(range?206:200,headers);if(req.method==='HEAD'){res.end();return;}
  const stream=createReadStream(clip,{start,end});streams.add(stream);stream.on('close',()=>streams.delete(stream));stream.on('error',error=>res.destroy(error));res.on('close',()=>stream.destroy());stream.pipe(res);
 });
 await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(9386,'127.0.0.1',resolve);});
 const probe=createServer();await new Promise((resolve,reject)=>{probe.once('error',reject);probe.listen(9387,'127.0.0.1',resolve);});await new Promise(resolve=>probe.close(resolve));
 const profile=join(root,'.omx/playback-browser-profile',Date.now()+'-'+process.pid);
 chrome=spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',[
  '--user-data-dir='+profile,'--remote-debugging-port=9387','--no-first-run','--no-default-browser-check',
  '--autoplay-policy=no-user-gesture-required','--window-position=70,60','--window-size=900,800','--new-window','http://127.0.0.1:9386'
 ],{stdio:'ignore'});
 let spawnError;chrome.once('error',error=>{spawnError=error;});let page;
 for(let i=0;i<150;i++){
  if(spawnError)throw spawnError;
  try{page=(await(await fetch('http://127.0.0.1:9387/json/list',{signal:AbortSignal.timeout(500)})).json()).find(p=>p.url==='http://127.0.0.1:9386/');if(page)break;}catch{}
  await wait(100);
 }
 if(!page)throw Error('Isolated playback Chrome did not become available');
 ws=new WebSocket(page.webSocketDebuggerUrl);await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('Chrome WebSocket connection timeout')),5000);ws.onopen=()=>{clearTimeout(timer);resolve();};ws.onerror=error=>{clearTimeout(timer);reject(error);};});
 let id=0;const pending=new Map();
 ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.method==='Media.playerPropertiesChanged')properties.push(m.params);if(m.id){const p=pending.get(m.id);if(p){pending.delete(m.id);clearTimeout(p.timer);m.error?p.reject(Error(JSON.stringify(m.error))):p.resolve(m.result);}}};
 ws.onclose=()=>{for(const p of pending.values()){clearTimeout(p.timer);p.reject(Error('Chrome connection closed'));}pending.clear();};
 call=(method,params={},timeout=650000)=>new Promise((resolve,reject)=>{const number=++id,timer=setTimeout(()=>{pending.delete(number);reject(Error(method+' timeout'));},timeout);pending.set(number,{resolve,reject,timer});ws.send(JSON.stringify({id:number,method,params}));});
 const version=await call('Browser.getVersion',{},5000);await call('Media.enable',{},5000);await call('Page.bringToFront',{},5000);
 let ready=false;for(let i=0;i<100;i++){const state=await call('Runtime.evaluate',{expression:'typeof window.playbackTest',returnByValue:true},5000);if(state.result.value==='function'){ready=true;break;}await wait(100);}if(!ready)throw Error('Playback test page did not finish loading');
 const result=await call('Runtime.evaluate',{expression:'window.playbackTest()',awaitPromise:true,returnByValue:true});
 if(result.exceptionDetails)throw Error(JSON.stringify(result.exceptionDetails));
 report={status:'completed',file:clip,chrome:version,playback:result.result.value,decoderProperties:properties};
 const screenshot=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:false},5000);
 writeFileSync(join(output,'playback.png'),Buffer.from(screenshot.data,'base64'));
}catch(error){report={status:'failed',file:clip,error:error.stack||String(error),decoderProperties:properties};process.exitCode=1;}
finally{
 writeFileSync(join(output,'report.json'),JSON.stringify(report,null,2));
 if(call&&ws?.readyState===WebSocket.OPEN)await call('Browser.close',{},2000).catch(()=>{});
 ws?.close();
 if(chrome&&chrome.pid&&chrome.exitCode===null){await wait(500);if(chrome.exitCode===null)await new Promise(resolve=>{const kill=spawn('taskkill',['/PID',String(chrome.pid),'/T','/F'],{stdio:'ignore'});kill.once('exit',resolve);kill.once('error',resolve);});}
 for(const stream of streams)stream.destroy();
 if(server){server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
 console.log(JSON.stringify({status:report?.status,report:join(output,'report.json'),quality:report?.playback&&{total:report.playback.totalVideoFrames,dropped:report.playback.droppedVideoFrames,callbacks:report.playback.requestVideoFrameCallbacks}}));
}
