import {createServer} from 'node:http';
import {readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {spawn} from 'node:child_process';
import {resolve,join} from 'node:path';

const root=resolve(import.meta.dirname,'..');
const executable=resolve(process.argv[2]||join(root,'bin/Release/net6.0-windows/FreeCamRecord.exe'));
const label=process.argv[3]||'browser';
const server=createServer((req,res)=>{res.setHeader('content-type','text/html; charset=utf-8');res.end(readFileSync(join(root,'tools/motion-test.html')));});
await new Promise(r=>server.listen(9376,'127.0.0.1',r));
const chrome=spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',[
  '--user-data-dir='+join(root,'.omx/browser-profile'),'--remote-debugging-port=9377','--no-first-run','--no-default-browser-check',
  '--disable-background-timer-throttling','--autoplay-policy=no-user-gesture-required','--window-position=70,60','--window-size=900,800','--app=http://127.0.0.1:9376'
],{stdio:'ignore'});
let ws,call;
try{
  let page;
  for(let i=0;i<100;i++){
    try{page=(await (await fetch('http://127.0.0.1:9377/json/list')).json()).find(p=>p.url.startsWith('http://127.0.0.1:9376'));if(page)break;}catch{}
    await new Promise(r=>setTimeout(r,100));
  }
  if(!page)throw Error('Test browser did not start');
  ws=new WebSocket(page.webSocketDebuggerUrl);await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j;});
  let id=0;const pending=new Map();
  ws.onmessage=e=>{const data=JSON.parse(e.data);if(data.id){const p=pending.get(data.id);pending.delete(data.id);data.error?p.reject(data.error):p.resolve(data.result);}};
  call=(method,params={})=>new Promise((resolve,reject)=>{const number=++id;pending.set(number,{resolve,reject});ws.send(JSON.stringify({id:number,method,params}));});
  await call('Page.bringToFront');
  await new Promise(r=>setTimeout(r,1500));
  const browserVersion=await call('Browser.getVersion');
  for(const fps of (process.argv[5]||'60,165').split(',').map(Number)){
    const directory=join(root,'.omx/verification',label+'-'+Date.now()+'-'+fps);mkdirSync(directory,{recursive:true});
    await call('Runtime.evaluate',{expression:'motionBench.reset()'});
    const gui=process.argv[6]==='gui';
    if(gui) await call('Runtime.evaluate',{expression:'motionBench.startTone?.()',awaitPromise:true});
    const test=gui
      ? spawn('powershell',['-NoProfile','-File',join(root,'tools/ui-record.ps1'),'-Executable',executable,'-OutputDirectory',directory,'-Fps',String(fps),'-Seconds','8'],{stdio:['ignore','pipe','pipe']})
      : spawn(executable,['--browser-test','0','0','0','0',String(fps),'6',directory],{stdio:'ignore',env:{...process.env,FREECAM_CAPTURE_BACKEND:process.argv[4]||'dxgi'}});
    if(gui){test.stdout.on('data',d=>process.stdout.write(d));test.stderr.on('data',d=>process.stderr.write(d));}
    const code=await new Promise((r,j)=>{test.on('exit',r);test.on('error',j);});
    const metrics=await call('Runtime.evaluate',{expression:'JSON.stringify(motionBench.stats())',returnByValue:true});
    writeFileSync(join(directory,'browser.json'),metrics.result.value);
    writeFileSync(join(directory,'chrome-version.json'),JSON.stringify(browserVersion,null,2));
    if(code!==0)throw Error('Recording failed');
    console.log(JSON.stringify({directory,exitCode:code,recording:JSON.parse(readFileSync(join(directory,'recording.json'),'utf8').replace(/^\uFEFF/,'')),source:JSON.parse(metrics.result.value).sourceFps}));
  }
  await call('Browser.close').catch(()=>{});
}finally{if(ws?.readyState===WebSocket.OPEN&&call)await Promise.race([call('Browser.close').catch(()=>{}),new Promise(r=>setTimeout(r,1000))]);ws?.close();server.close();}
