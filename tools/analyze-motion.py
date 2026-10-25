"""Development analysis: decode the browser's visible binary frame IDs from an actual recording."""
import io
import json
import math
import pathlib
import statistics
import subprocess
import sys
from PIL import Image, ImageChops

folder = pathlib.Path(sys.argv[1])
ffmpeg = sys.argv[2]
recording = json.loads((folder / 'recording.json').read_text(encoding='utf-8-sig'))
browser = json.loads((folder / 'browser.json').read_text(encoding='utf-8-sig'))
clip = recording['Path']

def decode(extra):
    return subprocess.run([ffmpeg, '-v', 'error', '-i', clip] + extra,
                          check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout

image = Image.open(io.BytesIO(decode(['-frames:v', '1', '-f', 'image2pipe', '-c:v', 'png', '-']))).convert('RGB')
image.save(folder / 'browser-first-frame.png')
r, g, b = image.crop((0, 0, image.width, min(350, image.height))).split()
mask = ImageChops.multiply(ImageChops.multiply(r.point(lambda v: 255 if v > 170 else 0),
                                              b.point(lambda v: 255 if v > 170 else 0)),
                           g.point(lambda v: 255 if v < 95 else 0))
bounds = mask.getbbox()
if not bounds:
    raise RuntimeError('Visible magenta frame-counter marker not found in the recorded web page')
dpr = browser['dpr']
x = round(bounds[0] + 20 * dpr)
y = round(bounds[1] + 16 * dpr)
width = math.ceil(24 * 14 * dpr)
raw = decode(['-an', '-vf', f'format=gray,crop={width}:1:{x}:{y}', '-pix_fmt', 'gray', '-f', 'rawvideo', '-'])
offsets = [min(width - 1, round((bit * 14 + 7) * dpr)) for bit in range(24)]
ids = [sum((row[offset] > 128) << bit for bit, offset in enumerate(offsets))
       for row in (raw[i:i + width] for i in range(0, len(raw), width)) if len(row) == width]
if len(ids) < 20:
    raise RuntimeError('Insufficient decoded frames to analyze the captured test page')
gaps = [b - a for a, b in zip(ids, ids[1:])]
runs = []
run = 1
for gap in gaps:
    if gap == 0:
        run += 1
    else:
        runs.append(run)
        run = 1
runs.append(run)
positive = [gap for gap in gaps if gap > 0]
fps = recording['Fps']
durations = sorted(run / fps * 1000 for run in runs)
report = {
    'file': clip, 'targetFps': fps, 'sourceFps': round(browser['sourceFps'], 2),
    'captureCallsPerSecond': round(recording['CaptureRate'], 2) if recording.get('CaptureRate') else None, 'outputFrames': len(ids),
    'distinctSourceFrames': len(set(ids)),
    'distinctFramesPerSecond': round(len(set(ids)) / recording['Seconds'], 2),
    'duplicateFramePercent': round(100 * sum(g == 0 for g in gaps) / len(gaps), 2),
    'longestRepeatedFrameMs': round(max(durations), 2),
    'p95RepeatedFrameMs': round(durations[min(len(durations) - 1, math.ceil(.95 * len(durations)) - 1)], 2),
    'largestSourceFrameJump': max(positive, default=0),
    'counterWentBackwards': any(g < 0 for g in gaps),
    'medianSourceAdvance': statistics.median(positive) if positive else 0, 'marker': bounds,
    'frameCounterAdvanced': ids[-1] > ids[0],
}
(folder / 'motion-analysis.json').write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False))
