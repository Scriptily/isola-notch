"""The Store banner loop: five 4.8 s scenes crossfaded (0.8 s), cut so the last frame
flows into the first, over a 20 s (ten-bar) music bed whose tail crossfades into its head."""
import subprocess, sys, os
FF = 'ffmpeg'
HERE = os.path.dirname(os.path.abspath(__file__))
os.chdir(HERE)
scenes = ['1-music', '2-call', '3-download', '4-home', '5-search']
order = scenes + [scenes[0]]
D, X = 4.8, 0.8
pre = 0.3                 # pre-roll of audio before the loop start, laid under the tail
bar0 = 32.0               # bar 16 after the drop, on the trailer's 120 BPM grid

# audio: [bar0 - pre, bar0 + 20]; the head (pre-roll) fades in under the last 0.3 s
a = (f"atrim=start=11.617,asetpts=PTS-STARTPTS,atempo=1.00134,"
     f"atrim=start={bar0 - pre}:end={bar0 + 20},asetpts=PTS-STARTPTS,highshelf=f=7000:g=-3,"
     f"asplit[h][b];"
     f"[h]atrim=end={pre},asetpts=PTS-STARTPTS,afade=t=in:d={pre},adelay={int((20 - pre) * 1000)}:all=1[hd];"
     f"[b]atrim=start={pre},asetpts=PTS-STARTPTS,afade=t=out:st={20 - pre}:d={pre}[bd];"
     f"[bd][hd]amix=inputs=2:normalize=0:duration=first,atrim=end=20,"
     f"loudnorm=I=-18:TP=-1.5:LRA=11")
subprocess.run([FF, '-y', '-v', 'error', '-i', 'tracks/69.mp3', '-filter_complex', a, '-ar', '48000', 'banner.wav'], check=True)

args = [FF, '-y', '-v', 'error']
for s in order:
    args += ['-framerate', '60', '-i', f'bannerframes/{s}/%04d.png']
args += ['-i', 'banner.wav']
f, last = [], '[0:v]'
for k in range(1, len(order)):
    out = f'[x{k}]'
    f.append(f"{last}[{k}:v]xfade=transition=fade:duration={X}:offset={k * (D - X):.3f}{out}")
    last = out
f.append(f"{last}trim=start={X}:end={X + 20},setpts=PTS-STARTPTS,format=yuv420p[v]")
args += ['-filter_complex', ';'.join(f), '-map', '[v]', '-map', f'{len(order)}:a',
         '-c:v', 'libx264', '-preset', 'slow', '-crf', '15', '-r', '60', '-movflags', '+faststart',
         '-c:a', 'aac', '-b:a', '320k', '-t', '20', 'isola-banner.mp4']
subprocess.run(args, check=True)
print('ok')
