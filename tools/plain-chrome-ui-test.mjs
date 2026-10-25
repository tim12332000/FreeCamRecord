import {createServer} from 'node:http';
import {readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {spawn} from 'node:child_process';
import {resolve,join} from 'node:path';
const root=resolve(import.meta.dirname,'..'),exe=resolve(process.argv[2]),label=process.argv[3]||'plain-ui',fps=process.argv[4]||'0';
const directory=join(root,'.omx/verification',label+'-'+Date.now());mkdirSync(directory,{recursive:true});
const metrics=[];
const server=createServer((req,res)=>{
 if(req.url==='/metrics'){let body='';req.on('data',b=>body+=b);req.on('end',()=>{try{metrics.push(JSON.parse(body));}catch{}res.end('ok');});return;}
 res.setHeader('content-type','text/html; charset=utf-8');res.end(readFileSync(join(root,'tools/motion-test.html')));
});
await new Promise(r=>server.listen(9380,'127.0.0.1',r));
const chrome=spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',[
 '--user-data-dir='+join(root,'.omx/plain-browser-profile'),'--no-first-run','--no-default-browser-check',
 '--new-window','--window-position=70,60','--window-size=900,800','http://127.0.0.1:9380/?plain'+(process.argv[5]==='focus'?'&focus':'')
],{stdio:'ignore'});
try{
 for(let i=0;i<100&&metrics.length===0;i++)await new Promise(r=>setTimeout(r,100));
 if(!metrics.length)throw Error('Ordinary Chrome test page did not load');
 const test=spawn('powershell',['-NoProfile','-File',join(root,'tools/ui-record.ps1'),'-Executable',exe,'-OutputDirectory',directory,'-Fps',fps,'-Seconds','10'],{stdio:'inherit'});
 const code=await new Promise((r,j)=>{test.on('exit',r);test.on('error',j);});
 writeFileSync(join(directory,'browser-samples.json'),JSON.stringify(metrics,null,2));
 writeFileSync(join(directory,'browser.json'),JSON.stringify(metrics.at(-1),null,2));
 console.log(JSON.stringify({directory,exitCode:code,metrics:metrics.map(m=>({time:m.seconds,fps:m.sourceFps,visibility:m.visibility,focused:m.focused,audio:m.audio}))}));
 if(code!==0)process.exitCode=1;
}finally{
 // Close only the dedicated Chrome test window using its fixed test-page title.
 await new Promise(resolve=>{const cleanup=spawn('powershell',['-NoProfile','-Command',"Get-Process chrome | Where-Object { $_.MainWindowTitle -like 'FreeCamRecord Motion Bench*' } | ForEach-Object { [void]$_.CloseMainWindow() }"],{stdio:'ignore'});cleanup.on('exit',resolve);});
 server.close();
}
