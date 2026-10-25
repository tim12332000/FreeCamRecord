"""Independent offline decoder checks for --timing-test; never opens/captures the desktop."""
import json, pathlib, re, struct, subprocess, sys
import numpy as np

folder=pathlib.Path(sys.argv[1]); decoder=sys.argv[2]
manifest=json.loads((folder/'manifest.json').read_text(encoding='utf-8-sig'))
def run(file,arguments):
    result=subprocess.run([decoder,'-hide_banner','-i',str(file)]+arguments,stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True)
    return result.stdout,result.stderr.decode('utf-8',errors='replace')
def atoms(data,start=0,end=None):
    end=len(data) if end is None else end
    while start+8<=end:
        size,kind=struct.unpack_from('>I4s',data,start); header=8
        if size==1:size=struct.unpack_from('>Q',data,start+8)[0];header=16
        if size==0:size=end-start
        if size<header or start+size>end:raise ValueError('Invalid MP4 atom')
        yield kind,start+header,start+size
        start+=size
def track_times(file):
    data=file.read_bytes(); result={}
    for kind,begin,end in atoms(data):
        if kind!=b'moov':continue
        for kind,begin,end in atoms(data,begin,end):
            if kind!=b'trak':continue
            for kind,begin,end in atoms(data,begin,end):
                if kind!=b'mdia':continue
                handler=None;duration=None
                for child,a,b in atoms(data,begin,end):
                    if child==b'hdlr':handler=data[a+8:a+12].decode('ascii')
                    if child==b'mdhd':
                        version=data[a]; offset=20 if version==1 else 12
                        rate=struct.unpack_from('>I',data,a+offset)[0]
                        length=struct.unpack_from('>Q' if version==1 else '>I',data,a+offset+4)[0]
                        duration=length/rate
                result[handler]=duration
    return result

reports=[]
for entry in manifest['Files']:
    file=folder/entry['Name']; fps=entry['Fps']; seconds=entry['Seconds']
    _,log=run(file,['-an','-vf','showinfo','-vsync','0','-f','null','-'])
    times=[float(t) for t in re.findall(r'\bn:\s*\d+\s+pts:.*?pts_time:([\d.]+)',log)]
    expected=[t/10_000_000 for t in entry['Timestamps']]
    if len(expected)==1 and seconds-expected[-1]>2/fps:expected.append(seconds-1/fps)
    assert len(times)==len(expected),(file.name,'unexpected hidden catch-up pictures',len(times),len(expected))
    assert all(abs(a-b)<.00005 for a,b in zip(times,expected)),(file.name,'PTS mismatch')
    raw,_=run(file,['-v','error','-an','-vsync','0','-pix_fmt','rgb24','-f','rawvideo','-'])
    images=np.frombuffer(raw,dtype=np.uint8).reshape(-1,48,64,3)
    assert len(images)==len(times)
    colors={'red':(255,0,0),'green':(0,255,0),'blue':(0,0,255),'yellow':(255,255,0)}
    for check in entry['ColorChecks']:
        index=max(i for i,t in enumerate(times) if t<=check['Time'])
        actual=images[index,10:30,10:40].mean(axis=(0,1))
        assert max(abs(actual-np.array(colors[check['Color']])))<35,(file.name,'wrong old-picture hold',check,actual)
    durations=track_times(file) if file.suffix=='.mp4' else {'vide':seconds}
    assert abs(durations['vide']-seconds)<.02,(file.name,'wrong physical video duration',durations)
    audio_report=None
    if entry['Audio']:
        raw_audio,_=run(file,['-v','error','-vn','-ac','1','-ar','48000','-f','s16le','-'])
        samples=np.frombuffer(raw_audio,dtype='<i2').astype(float)
        assert abs(len(samples)/48000-seconds)<.045,(file.name,'audio duration drift')
        assert entry['AudioInputFrames']==seconds*48000
        rms=np.sqrt((samples[:len(samples)//480*480].reshape(-1,480)**2).mean(axis=1))
        on=rms>max(30,rms.max()*.3)
        starts=[i*.01 for i,value in enumerate(on) if value and (i==0 or not on[i-1])]
        offsets=[min(starts,key=lambda p:abs(p-t))-t for t in (.6,1.6,2.6)]
        assert max(map(abs,offsets))<.04,(file.name,'audio pulse moved across a video gap',starts)
        audio_report={'decodedSeconds':len(samples)/48000,'pulseSeconds':starts,'maxPulseOffsetMs':max(map(abs,offsets))*1000}
    reports.append({'file':file.name,'inputPictures':entry['InputPictures'],'decodedPictures':len(times),
                    'physicalSeconds':seconds,'trackDuration':durations,'skippedSlots':entry['SkippedSlots'],
                    'largestPtsGapMs':max((b-a)*1000 for a,b in zip(times,times[1:])) if len(times)>1 else 0,
                    'audio':audio_report,'pass':True})
result={'pass':True,'schedulerAssertions':manifest['Checks'],'files':reports}
(folder/'decoder-verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result))
