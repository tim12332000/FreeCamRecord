"""Check the browser fixture's white flash against its recorded 880Hz beep."""
import json, pathlib, subprocess, sys
import numpy as np

folder = pathlib.Path(sys.argv[1])
tool = sys.argv[2]
record = json.loads((folder/'recording.json').read_text(encoding='utf-8-sig'))
source = json.loads((folder/'browser.json').read_text(encoding='utf-8-sig'))
motion = json.loads((folder/'motion-analysis.json').read_text(encoding='utf-8-sig'))
def decode(parameters):
    return subprocess.run([tool,'-v','error','-i',record['Path']]+parameters,
                          check=True,stdout=subprocess.PIPE,stderr=subprocess.PIPE).stdout
dpr=source['dpr']
x=round(motion['marker'][0]-16*dpr+(source['innerWidth']-32)*dpr)
y=round(motion['marker'][1]+16*dpr)
video=np.frombuffer(decode(['-an','-vf',f'format=gray,crop=1:1:{x}:{y}','-pix_fmt','gray','-f','rawvideo','-']),dtype=np.uint8)
wave=np.frombuffer(decode(['-vn','-ac','1','-ar','48000','-f','s16le','-']),dtype='<i2').astype(np.float64)
blocks=wave[:len(wave)//480*480].reshape(-1,480)
rms=np.sqrt((blocks*blocks).mean(axis=1))
threshold=max(8,float(np.percentile(rms,99))*.3)
def starts(values,period):
    return [i*period for i,state in enumerate(values) if state and (i==0 or not values[i-1])]
visual=starts(video>190,1/record['Fps'])
audible=starts(rms>threshold,.01)
offsets=[min(audible,key=lambda a:abs(a-v))-v for v in visual if v>.2 and v<(len(video)/record['Fps']-.2)] if audible else []
report={'videoFlashSeconds':visual,'audioBeepSeconds':audible,'audioPeakRms':float(rms.max()),
        'audioMinusVideoOffsetsMs':[round(v*1000,2) for v in offsets],
        'maxAbsOffsetMs':round(max(map(abs,offsets))*1000,2) if offsets else None,
        'pass':len(offsets)>=4 and max(map(abs,offsets))<.15}
(folder/'av-analysis.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report))
if not report['pass']:sys.exit(1)
