"""
Placeholder sound for Templar Knights: every effect and music track is synthesised here from sine
waves, noise and envelopes (no samples, no recordings), so it is all ours.

    python tools/audio/placeholder_audio.py            # make what's missing
    python tools/audio/placeholder_audio.py --force    # remake everything

Effects go to assets/sound/*.wav (22 kHz mono, 16-bit). Music goes to assets/music/*.ogg (made with
ffmpeg's Vorbis encoder; if ffmpeg is missing, .wav files are written instead and the game plays those).
Needs numpy.
"""
import os, shutil, subprocess, sys, wave
import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SR = 22050
rng = np.random.default_rng(1307)

def t(seconds): return np.arange(int(seconds * SR)) / SR

def env(n, attack=0.005, decay=0.3, curve=4.0):
    x = np.arange(n) / SR
    a = np.clip(x / max(attack, 1e-4), 0, 1)
    d = np.exp(-curve * np.maximum(0, x - attack) / max(decay, 1e-4))
    return a * d

def noise(seconds): return rng.uniform(-1, 1, int(seconds * SR))

def lowpass(x, cutoff):
    # One-pole low-pass, good enough for thuds and whooshes.
    a = np.exp(-2 * np.pi * cutoff / SR)
    y = np.empty_like(x); acc = 0.0
    for i, v in enumerate(x):
        acc = (1 - a) * v + a * acc; y[i] = acc
    return y

def highpass(x, cutoff): return x - lowpass(x, cutoff)

def sweep(f0, f1, seconds):
    tt = t(seconds); f = np.linspace(f0, f1, len(tt))
    return np.sin(2 * np.pi * np.cumsum(f) / SR)

def bell(freq, seconds, partials=((1, 1), (2.76, 0.5), (5.4, 0.25), (8.9, 0.12))):
    tt = t(seconds)
    return sum(a * np.sin(2 * np.pi * freq * m * tt) * np.exp(-tt * (2.2 + m * 0.8)) for m, a in partials)

def pluck(freq, seconds, bright=0.6):
    # Karplus-Strong string.
    n = int(seconds * SR); period = max(2, int(SR / freq))
    buf = rng.uniform(-1, 1, period) * bright
    out = np.empty(n)
    for i in range(n):
        out[i] = buf[i % period]
        buf[i % period] = 0.5 * (buf[i % period] + buf[(i + 1) % period]) * 0.996
    return out

def organ(freq, seconds):
    tt = t(seconds)
    s = sum(a * np.sin(2 * np.pi * freq * m * tt) for m, a in ((1, 1), (2, 0.5), (3, 0.3), (4, 0.15)))
    return s * env(len(tt), attack=0.08, decay=seconds, curve=0.6)

def norm(x, peak=0.85):
    m = np.max(np.abs(x)) or 1
    return x / m * peak

def write_wav(path, x):
    x = np.clip(x, -1, 1)
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes((x * 32767).astype("<i2").tobytes())

def mix(*parts):
    n = max(len(p) for p in parts)
    out = np.zeros(n)
    for p in parts: out[:len(p)] += p
    return out

def at(x, seconds, total):
    out = np.zeros(int(total * SR)); i = int(seconds * SR)
    out[i:i + len(x)] += x[:max(0, len(out) - i)]
    return out

# ---------------------------------------------------------------- effects

def sfx():
    s = {}
    n = noise(0.25)
    s["swing"] = norm(highpass(n, 900) * env(len(n), 0.03, 0.12, 3), 0.5)
    hit = mix(lowpass(noise(0.18), 900) * env(int(0.18 * SR), 0.002, 0.07), 0.6 * sweep(180, 60, 0.18) * env(int(0.18 * SR), 0.002, 0.08))
    s["hit"] = norm(hit, 0.8)
    s["crit"] = norm(mix(hit, 0.5 * bell(1400, 0.4)), 0.9)
    clang = mix(bell(900, 0.5, ((1, 1), (2.3, 0.7), (3.9, 0.5), (6.1, 0.3))), 0.3 * highpass(noise(0.05), 2000))
    s["block"] = norm(clang, 0.7)
    s["hurt"] = norm(lowpass(noise(0.22), 500) * env(int(0.22 * SR), 0.002, 0.09) + 0.4 * sweep(220, 110, 0.22) * env(int(0.22 * SR), 0.01, 0.1), 0.7)
    s["monster_die"] = norm(mix(lowpass(noise(0.6), 400) * env(int(0.6 * SR), 0.01, 0.35, 2), 0.5 * sweep(160, 50, 0.6) * env(int(0.6 * SR), 0.01, 0.4, 2)), 0.7)
    s["player_die"] = norm(mix(*(at(bell(f, 1.6), i * 0.35, 2.6) for i, f in enumerate((440, 349, 294, 220)))), 0.7)
    s["level_up"] = norm(mix(*(at(bell(f, 1.2), i * 0.12, 1.8) for i, f in enumerate((523, 659, 784, 1047)))), 0.75)
    s["mastery_up"] = norm(mix(at(bell(784, 0.8), 0, 1.0), at(bell(1175, 0.8), 0.1, 1.0)), 0.55)
    s["loot"] = norm(mix(at(bell(1319, 0.25), 0, 0.35), at(bell(1760, 0.25), 0.06, 0.35)), 0.45)
    coins = mix(*(at(bell(rng.uniform(2200, 3400), 0.18, ((1, 1), (2.1, 0.4))), i * rng.uniform(0.03, 0.06), 0.5) for i in range(6)))
    s["gold"] = norm(coins, 0.5)
    s["cast_bolt"] = norm(sweep(300, 1400, 0.35) * env(int(0.35 * SR), 0.01, 0.2) + 0.3 * highpass(noise(0.35), 3000) * env(int(0.35 * SR), 0.01, 0.15), 0.6)
    s["cast_burst"] = norm(lowpass(noise(0.7), 1500) * env(int(0.7 * SR), 0.05, 0.4, 2) + 0.5 * sweep(120, 60, 0.7) * env(int(0.7 * SR), 0.02, 0.4), 0.8)
    s["cast_nova"] = norm(mix(lowpass(noise(0.9), 700) * env(int(0.9 * SR), 0.1, 0.5, 2), 0.5 * sweep(80, 260, 0.9) * env(int(0.9 * SR), 0.1, 0.5, 2)), 0.8)
    s["cast_heal"] = norm(mix(*(at(organ(f, 1.0) * env(int(1.0 * SR), 0.15, 0.8, 2), i * 0.08, 1.4) for i, f in enumerate((523, 659, 784)))), 0.5)
    s["cast_buff"] = norm(mix(at(bell(392, 1.0), 0, 1.2), at(bell(587, 1.0), 0.05, 1.2)), 0.5)
    s["cast_drain"] = norm(sweep(900, 200, 0.6) * env(int(0.6 * SR), 0.05, 0.4) * (0.6 + 0.4 * np.sin(2 * np.pi * 18 * t(0.6))), 0.6)
    s["cast_blink"] = norm(sweep(2000, 400, 0.25) * env(int(0.25 * SR), 0.005, 0.12), 0.5)
    s["cast_revive"] = norm(mix(*(at(organ(f, 1.6) * env(int(1.6 * SR), 0.3, 1.2, 1.5), i * 0.2, 2.4) for i, f in enumerate((392, 494, 587, 784)))), 0.6)
    s["telegraph"] = norm(sweep(600, 650, 0.5) * (0.5 + 0.5 * np.sign(np.sin(2 * np.pi * 9 * t(0.5)))) * env(int(0.5 * SR), 0.01, 0.4, 1), 0.35)
    horn = sum(a * np.sin(2 * np.pi * 110 * m * t(1.6)) for m, a in ((1, 1), (2, 0.6), (3, 0.4), (4, 0.2), (5, 0.1)))
    s["boss_phase"] = norm(horn * env(len(horn), 0.15, 1.2, 1.5), 0.7)
    s["quest_complete"] = norm(mix(*(at(organ(f, 0.9), i * 0.18, 1.6) for i, f in enumerate((392, 523, 659, 784)))), 0.6)
    s["ui_click"] = norm(highpass(noise(0.03), 2500) * env(int(0.03 * SR), 0.001, 0.01), 0.35)
    s["ui_open"] = norm(lowpass(noise(0.18), 1200) * env(int(0.18 * SR), 0.02, 0.08), 0.35)
    s["page"] = norm(highpass(noise(0.3), 1500) * env(int(0.3 * SR), 0.05, 0.12, 2), 0.3)
    s["alarm"] = norm(mix(*(at(bell(f, 0.4), i * 0.18, 1.3) for i, f in enumerate((988, 784, 988, 784, 988, 784)))), 0.6)
    s["potion"] = norm(mix(*(at(np.sin(2 * np.pi * rng.uniform(500, 900) * t(0.06)) * env(int(0.06 * SR), 0.002, 0.03), i * 0.07, 0.6) for i in range(7))), 0.4)
    s["waypoint"] = norm(mix(at(bell(523, 1.5), 0, 1.8), at(bell(784, 1.5), 0.15, 1.8), at(bell(1047, 1.5), 0.3, 1.8)), 0.5)
    s["duel_start"] = norm(mix(at(clang, 0, 1.0), at(clang, 0.35, 1.0)), 0.7)
    return s

# ---------------------------------------------------------------- music

MODES = {"dorian": [0, 2, 3, 5, 7, 9, 10], "phrygian": [0, 1, 3, 5, 7, 8, 10], "aeolian": [0, 2, 3, 5, 7, 8, 10], "mixolydian": [0, 2, 4, 5, 7, 9, 10]}

def note(root, mode, degree):
    sc = MODES[mode]
    octave, idx = divmod(degree, len(sc))
    return root * 2 ** ((sc[idx] + 12 * octave) / 12)

def track(name, root, mode, bpm, seconds, kind):
    """kind: town (organ drone, slow plucked melody), field (drum, drone, faster tune), prologue (bells, fire)."""
    total = int(seconds * SR)
    out = np.zeros(total)
    beat = 60 / bpm
    r = np.random.default_rng(abs(hash(name)) % (2 ** 32))
    # Drone: root and fifth, slowly breathing.
    tt = np.arange(total) / SR
    drone = (np.sin(2 * np.pi * root / 2 * tt) + 0.6 * np.sin(2 * np.pi * root * 3 / 4 * tt) + 0.3 * np.sin(2 * np.pi * root * tt))
    drone *= 0.18 * (0.75 + 0.25 * np.sin(2 * np.pi * tt / 9))
    out += drone
    # Melody: a random walk on the mode, in phrases of 8 beats with rests.
    deg, pos = 7, 0.0
    while pos < seconds - 2:
        for _ in range(int(r.integers(4, 8))):
            deg = int(np.clip(deg + r.choice([-2, -1, -1, 0, 1, 1, 2]), 3, 13))
            dur = beat * r.choice([1, 1, 2, 0.5] if kind == "field" else [1, 2, 2, 3])
            f = note(root, mode, deg)
            voice = pluck(f, min(dur * 2.5, 3.0), 0.5) if kind != "prologue" else bell(f, 2.5) * 0.6
            i = int(pos * SR)
            seg = voice[:max(0, total - i)]
            out[i:i + len(seg)] += 0.32 * seg
            pos += dur
        pos += beat * r.choice([2, 3, 4])
    if kind == "field":
        # A frame drum on the beat, accent every fourth.
        for k in range(int(seconds / beat)):
            i = int(k * beat * SR)
            hit = lowpass(noise(0.25), 220) * env(int(0.25 * SR), 0.003, 0.12) * (1.0 if k % 4 == 0 else 0.55)
            seg = hit[:max(0, total - i)]
            out[i:i + len(seg)] += 0.9 * seg
    if kind == "prologue":
        crackle = highpass(noise(seconds), 2500) * 0.02 * (r.random(total) < 0.02)
        out += crackle + 0.04 * lowpass(noise(seconds), 300)
    # Fade the ends so the loop seams are soft.
    f = int(1.5 * SR)
    out[:f] *= np.linspace(0, 1, f); out[-f:] *= np.linspace(1, 0, f)
    return norm(out, 0.7)

TRACKS = {
    "prologue": (220.0, "phrygian", 60, 50, "prologue"),
    "town_act1": (196.0, "dorian", 66, 80, "town"), "act1_field": (196.0, "aeolian", 96, 70, "field"),
    "town_act2": (174.6, "aeolian", 64, 80, "town"), "act2_field": (174.6, "phrygian", 100, 70, "field"),
    "town_act3": (220.0, "mixolydian", 70, 80, "town"), "act3_field": (220.0, "dorian", 104, 70, "field"),
    "town_act4": (164.8, "aeolian", 58, 80, "town"), "act4_field": (164.8, "phrygian", 112, 70, "field"),
    "boss": (146.8, "phrygian", 120, 60, "field"),
}

def main(force=False):
    sdir, mdir = os.path.join(ROOT, "assets", "sound"), os.path.join(ROOT, "assets", "music")
    os.makedirs(sdir, exist_ok=True); os.makedirs(mdir, exist_ok=True)
    made = 0
    for name, x in sfx().items():
        p = os.path.join(sdir, name + ".wav")
        if force or not os.path.exists(p): write_wav(p, x); made += 1
    ffmpeg = shutil.which("ffmpeg")
    for name, (root, mode, bpm, secs, kind) in TRACKS.items():
        ogg, wav = os.path.join(mdir, name + ".ogg"), os.path.join(mdir, name + ".wav")
        if not force and (os.path.exists(ogg) or os.path.exists(wav)): continue
        x = track(name, root, mode, bpm, secs, kind)
        write_wav(wav, x)
        if ffmpeg:
            subprocess.run([ffmpeg, "-y", "-loglevel", "error", "-i", wav, "-c:a", "libvorbis", "-q:a", "3", ogg], check=True)
            os.remove(wav)
        made += 1
    print(f"audio: made {made} file(s)")

if __name__ == "__main__":
    main(force="--force" in sys.argv)
