"""
The 0.7 sound pass for Templar Knights: looping ambience for every kind of place, and richer combat and spell effects.
Everything is synthesised here from noise, oscillators and filters (no samples, no recordings).

    python tools/audio/sound_pass.py

Ambience loops go to assets/sound/amb_<name>.ogg (seamless, about 24 s). Effects replace the placeholder ones in
assets/sound/*.wav (hit, crit, swing, block, hurt, monster_die and the cast_* sounds) and add step_1..4.
Needs numpy, scipy and ffmpeg (for the .ogg loops; without it they are written as .wav).
"""
from __future__ import annotations

import os
import shutil
import subprocess
import wave

import numpy as np
from scipy import signal

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "assets", "sound")
SR = 22050
rng = np.random.default_rng(2026)


def secs(n): return int(n * SR)
def noise(s): return rng.standard_normal(secs(s))
def tt(s): return np.arange(secs(s)) / SR


def bp(x, lo, hi, order=2): return signal.sosfilt(signal.butter(order, [lo, hi], "bandpass", fs=SR, output="sos"), x)
def lp(x, f, order=2): return signal.sosfilt(signal.butter(order, f, "lowpass", fs=SR, output="sos"), x)
def hp(x, f, order=2): return signal.sosfilt(signal.butter(order, f, "highpass", fs=SR, output="sos"), x)


def brown(s):
    b = np.cumsum(noise(s)); b -= lp(b, 2.0, 1)
    return b / (np.abs(b).max() + 1e-9)


def slow(s, rate, lo=0.0, hi=1.0, seed=0):
    """A slowly wandering control signal between lo and hi (gusts, swells)."""
    r = np.random.default_rng(seed)
    n = max(4, int(s * rate) + 3)
    pts = r.uniform(lo, hi, n)
    x = np.linspace(0, n - 3, secs(s))
    i = np.floor(x).astype(int); f = x - i
    return pts[i] * (1 - f) + pts[i + 1] * f


def env(n, a=0.003, d=0.2, curve=5.0):
    x = np.arange(n) / SR
    return np.clip(x / max(a, 1e-4), 0, 1) * np.exp(-curve * np.maximum(0, x - a) / max(d, 1e-4))


def place(buf, x, at):
    i = int(at * SR); j = min(len(buf), i + len(x))
    if i < len(buf): buf[i:j] += x[:j - i]


def loop(x, fade=2.0):
    """Seamless loop: the tail is crossfaded into the head."""
    n = secs(fade)
    head, tail = x[:n].copy(), x[-n:].copy()
    w = np.linspace(0, 1, n)
    out = x[:-n].copy()
    out[:n] = head * w + tail * (1 - w)
    return out


def norm(x, peak=0.8):
    m = np.abs(x).max()
    return x * (peak / m) if m > 0 else x


def write(name, x, ogg=False):
    x = np.clip(x, -1, 1)
    pcm = (x * 32767).astype(np.int16)
    wav = os.path.join(OUT, name + ".wav")
    with wave.open(wav, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR); w.writeframes(pcm.tobytes())
    if ogg and shutil.which("ffmpeg"):
        oggp = os.path.join(OUT, name + ".ogg")
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", wav, "-c:a", "libvorbis", "-q:a", "3", oggp], check=True)
        os.remove(wav)
    print("[sound]", name)

# ----------------------------------------------------------------------------------------------- ambience

L = 26.0


def wind_raw(cold=False):
    g = slow(L, 0.35, 0.25, 1.0, 1)
    body = bp(brown(L), 80, 600) * (0.5 + 0.5 * g)
    hiss = bp(noise(L), 900 if cold else 600, 4000 if cold else 2200) * (g ** 2) * (0.22 if cold else 0.08)
    x = body + hiss
    if cold:  # thin whistles in the gusts
        f = 700 + 500 * slow(L, 0.2, 0, 1, 7)
        x += np.sin(2 * np.pi * np.cumsum(f) / SR) * (g ** 3) * 0.05
    return norm(x, 0.5)


def wind(cold=False): return loop(wind_raw(cold))


def rain():
    x = hp(noise(L), 1200) * 0.18 + bp(noise(L), 300, 1200) * 0.12
    for _ in range(int(L * 60)):
        d = bp(noise(0.02), 2500, 7000) * env(secs(0.02), 0.0005, 0.006) * rng.uniform(0.05, 0.25)
        place(x, d, rng.uniform(0, L - 0.05))
    x += lp(brown(L), 200) * 0.25  # distant rumble of the downpour
    return loop(norm(x, 0.55))


def fire_raw():
    roar = lp(brown(L), 300) * (0.6 + 0.4 * slow(L, 1.2, 0, 1, 3))
    x = roar * 0.6 + bp(noise(L), 500, 2500) * 0.04 * slow(L, 2, 0.3, 1, 4)
    for _ in range(int(L * 9)):
        n = secs(rng.uniform(0.004, 0.02))
        pop = bp(rng.standard_normal(n), 1500, 6000) * env(n, 0.0003, 0.004, 6) * rng.uniform(0.2, 0.9)
        place(x, pop, rng.uniform(0, L - 0.05))
    return norm(x, 0.6)


def fire(): return loop(fire_raw())


def drip(freq):
    n = secs(0.35)
    f = freq * (1 + 1.5 * np.exp(-np.arange(n) / (SR * 0.01)))
    s = np.sin(2 * np.pi * np.cumsum(f) / SR) * env(n, 0.001, 0.06)
    echo = np.zeros(n * 3); echo[:n] += s
    for k, g in ((int(0.11 * SR), 0.35), (int(0.23 * SR), 0.18), (int(0.37 * SR), 0.08)): echo[k:k + n] += s * g
    return echo


def crypt():
    x = lp(brown(L), 140) * 0.7 + bp(noise(L), 60, 220) * 0.15 * slow(L, 0.15, 0.5, 1, 5)
    for _ in range(int(L * 0.7)):
        place(x, drip(rng.uniform(900, 1700)) * rng.uniform(0.08, 0.2), rng.uniform(0, L - 1))
    return loop(norm(x, 0.45))


def chirp(f0):
    n = secs(rng.uniform(0.08, 0.16))
    k = np.arange(n) / n
    f = f0 * (1 + 0.35 * np.sin(np.pi * k)) * (1 + 0.03 * np.sin(2 * np.pi * 38 * k))
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * np.sin(np.pi * k) ** 2


def forest():
    leaves = bp(noise(L), 1200, 5000) * 0.05 * (0.4 + slow(L, 0.4, 0, 1, 9))
    x = wind_raw() * 0.5 + leaves
    t0 = 0.5
    while t0 < L - 2:
        f0 = rng.uniform(2200, 4200)
        for k in range(rng.integers(2, 6)):
            place(x, chirp(f0 * rng.uniform(0.93, 1.07)) * 0.05, t0 + k * rng.uniform(0.12, 0.22))
        t0 += rng.uniform(1.5, 4.5)
    return loop(norm(x, 0.5))


def crickets(x, level=0.035):
    for c in range(3):
        f = rng.uniform(4200, 5200); period = rng.uniform(0.45, 0.8); off = rng.uniform(0, period)
        n = secs(0.12)
        k = np.arange(n) / SR
        chirp_ = np.sin(2 * np.pi * f * k) * (0.5 + 0.5 * np.sign(np.sin(2 * np.pi * 30 * k))) * env(n, 0.005, 0.08, 2)
        t0 = off
        while t0 < L - 0.2:
            place(x, chirp_ * level * rng.uniform(0.6, 1), t0); t0 += period
    return x


def camp():
    return loop(norm(crickets(fire_raw() * 0.6 + wind_raw() * 0.3), 0.55))


def night_garden():
    return loop(norm(crickets(wind_raw() * 0.4, 0.05), 0.45))


def bell(f, s=4.0):
    k = tt(s)
    return sum(a * np.sin(2 * np.pi * f * m * k) * np.exp(-k * (1.2 + m * 0.6)) for m, a in ((1, 1), (2.0, 0.4), (2.76, 0.5), (5.4, 0.2)))


def town():
    x = wind_raw() * 0.5 + bp(noise(L), 200, 900) * 0.04 * slow(L, 0.5, 0.2, 1, 11)
    for at in (3.0, 15.5):
        for k in range(3): place(x, lp(bell(196 * 0.75), 2500) * 0.12, at + k * 2.2)
    return loop(norm(x, 0.5))


def hall():
    x = lp(brown(L), 220) * 0.6 + bp(noise(L), 150, 700) * 0.05
    x = x + wind_raw() * 0.15
    return loop(norm(x, 0.4))

def heartbeat():
    """Lub-dub at about 72 beats a minute, for when the hero is close to death. Exactly 8 beats, so it loops clean."""
    beat = 60 / 72
    n = secs(beat * 8)
    x = np.zeros(n)
    for k in range(8):
        place(x, thump(48, 0.18) * 0.9, k * beat)
        place(x, thump(42, 0.16) * 0.6, k * beat + 0.28)
    return norm(lp(x, 160), 0.8)


# ----------------------------------------------------------------------------------------------- effects

def thump(f=70, s=0.25):
    n = secs(s)
    fr = f * (1 + 2.5 * np.exp(-np.arange(n) / (SR * 0.015)))
    return np.sin(2 * np.pi * np.cumsum(fr) / SR) * env(n, 0.001, s * 0.4)


def crunch(s=0.15, lo=800, hi=5000):
    n = secs(s)
    return bp(rng.standard_normal(n), lo, hi) * env(n, 0.0008, s * 0.3)


def ring(f, s=0.6, decay=4.0):
    k = tt(s)
    return sum(a * np.sin(2 * np.pi * f * m * k + rng.uniform(0, 6)) for m, a in ((1, 1), (2.71, 0.6), (4.13, 0.35), (6.2, 0.2))) * np.exp(-k * decay)


def whoosh(s=0.28, lo=300, hi=2600):
    n = secs(s)
    k = np.arange(n) / n
    sweep = bp(rng.standard_normal(n), lo, hi)
    return sweep * np.sin(np.pi * k) ** 1.5


def pad(x, s): return np.concatenate([x, np.zeros(max(0, secs(s) - len(x)))])


def mixn(*parts):
    n = max(len(p) for p in parts)
    return sum(np.pad(p, (0, n - len(p))) for p in parts)


def sfx():
    write("hit", norm(mixn(thump(85, 0.22) * 0.9, crunch(0.12, 600, 3500) * 0.7, ring(1450, 0.18, 22) * 0.12), 0.8))
    write("crit", norm(mixn(thump(60, 0.35), crunch(0.2, 400, 5000) * 0.9, ring(980, 0.5, 7) * 0.3, whoosh(0.15, 2000, 6000) * 0.3), 0.85))
    write("swing", norm(whoosh(0.26, 400, 3200), 0.55))
    write("block", norm(mixn(ring(620, 0.7, 5) * 0.8, ring(1830, 0.4, 9) * 0.4, thump(110, 0.12) * 0.6, crunch(0.05, 2000, 7000) * 0.4), 0.8))
    write("hurt", norm(mixn(thump(55, 0.3), crunch(0.18, 200, 1800) * 0.6), 0.8))
    die_n = secs(0.9)
    groan = np.sin(2 * np.pi * np.cumsum(np.linspace(140, 70, die_n)) / SR) * env(die_n, 0.02, 0.5, 3) * 0.4
    write("monster_die", norm(mixn(thump(50, 0.5), crunch(0.4, 150, 1500) * 0.5, lp(groan + 0.3 * rng.standard_normal(die_n) * env(die_n, 0.02, 0.4), 900)), 0.8))
    # Spells: a whoosh in, a coloured body, a tail.
    fire_ = lp(brown(0.7), 900) * env(secs(0.7), 0.04, 0.35, 3)
    write("cast_bolt", norm(mixn(whoosh(0.35, 500, 4000) * 0.7, fire_ * 0.8, crunch(0.1, 2000, 8000) * 0.2), 0.75))
    boom = mixn(thump(45, 0.8), lp(brown(1.2), 500) * env(secs(1.2), 0.005, 0.6, 3), crunch(0.3, 300, 4000) * 0.5)
    write("cast_burst", norm(boom, 0.85))
    k = tt(1.0)
    shimmer = sum(np.sin(2 * np.pi * f * k) for f in (880, 1320, 1760, 2640)) * np.exp(-k * 3) * 0.2
    write("cast_nova", norm(mixn(whoosh(0.5, 200, 5000), shimmer, thump(55, 0.4) * 0.6), 0.8))
    k = tt(1.4)
    chord = sum(np.sin(2 * np.pi * f * k) * np.exp(-k * 1.6) for f in (523.3, 659.3, 784.0, 1046.5))
    write("cast_heal", norm(chord * np.clip(k / 0.05, 0, 1) * 0.5 + bp(noise(1.4), 3000, 8000) * np.exp(-k * 3) * 0.05, 0.6))
    k = tt(1.0)
    hum = (np.sin(2 * np.pi * 220 * k) + 0.5 * np.sin(2 * np.pi * 330 * k)) * np.sin(np.pi * k / 1.0) * 0.4
    write("cast_buff", norm(mixn(hum, whoosh(0.6, 300, 1500) * 0.4, ring(660, 1.0, 2.5) * 0.25), 0.6))
    k = tt(0.8)
    suck = np.sin(2 * np.pi * np.cumsum(np.linspace(900, 180, len(k))) / SR) * np.sin(np.pi * k / 0.8) * 0.4
    write("cast_drain", norm(mixn(suck, bp(noise(0.8), 200, 1200) * np.sin(np.pi * k / 0.8) * 0.4), 0.65))
    write("cast_blink", norm(mixn(whoosh(0.18, 1500, 7000), ring(1760, 0.3, 10) * 0.4), 0.6))
    # Footsteps on packed ground: four variants so they never repeat exactly.
    for i in range(4):
        step = mixn(thump(rng.uniform(90, 130), 0.09) * 0.7, crunch(0.07, rng.uniform(500, 900), rng.uniform(2500, 4000)) * 0.6)
        write(f"step_{i + 1}", norm(step, 0.5))


def endgame_sfx():
    """0.8: the forge room, runes, the Mist and the world boss horn."""
    # The hammer on the anvil: a hard metallic strike with a long bright ring.
    write("anvil_strike", norm(mixn(ring(1180, 1.4, 2.6) * 0.8, ring(2950, 0.9, 4.5) * 0.35, thump(140, 0.08) * 0.7, crunch(0.04, 3000, 9000) * 0.6), 0.85))
    # Magic answering: a rising shimmer that opens into a bright chord.
    k = tt(2.2)
    rise = np.sin(2 * np.pi * np.cumsum(np.linspace(300, 900, len(k))) / SR) * np.clip(k / 0.6, 0, 1) * np.exp(-np.clip(k - 0.6, 0, None) * 2) * 0.3
    chord = sum(np.sin(2 * np.pi * f * k) for f in (523.3, 659.3, 784.0, 1046.5, 1568.0)) * np.clip((k - 0.45) / 0.05, 0, 1) * np.exp(-np.clip(k - 0.45, 0, None) * 1.8) * 0.18
    sparkle = bp(noise(2.2), 4000, 9000) * np.exp(-k * 1.5) * 0.08
    write("forge_magic", norm(mixn(rise, chord, sparkle, whoosh(0.8, 300, 4000) * 0.4), 0.8))
    # The steel giving way: a crack, a dull clank and a hiss of smoke.
    k = tt(1.6)
    hiss = bp(noise(1.6), 1500, 7000) * np.exp(-k * 1.6) * 0.35
    write("forge_break", norm(mixn(crunch(0.25, 500, 6000), thump(60, 0.4) * 0.8, ring(410, 0.5, 9) * 0.4, hiss), 0.85))
    # Quenching: steam.
    k = tt(1.3)
    write("forge_quench", norm(mixn(bp(noise(1.3), 2500, 8000) * np.sin(np.pi * np.clip(k / 1.3, 0, 1)) ** 0.5 * np.exp(-k * 1.2), thump(90, 0.1) * 0.3), 0.6))
    # A coffer lid: wood creak and a cold chime.
    k = tt(1.2)
    creak = np.sin(2 * np.pi * np.cumsum(np.linspace(180, 260, len(k))) / SR) * (0.5 + 0.5 * np.sign(np.sin(2 * np.pi * 34 * k))) * np.exp(-k * 3) * 0.3
    write("coffer_open", norm(mixn(lp(creak, 1800), ring(1320, 1.2, 3) * 0.3, ring(1980, 1.0, 3.5) * 0.2), 0.7))
    # Setting a rune: stone on steel, a soft glow.
    write("rune_set", norm(mixn(thump(160, 0.06) * 0.6, ring(880, 0.8, 4) * 0.3, ring(1320, 0.6, 5) * 0.2), 0.6))
    write("rune_found", norm(mixn(ring(1046, 0.9, 3.5) * 0.4, ring(1568, 0.8, 4) * 0.3, whoosh(0.3, 2000, 7000) * 0.3), 0.6))
    # The hunting horn: two long notes with breath.
    k = tt(2.4)
    def note(f, a, b):
        e = np.clip((k - a) / 0.08, 0, 1) * np.clip((b - k) / 0.2, 0, 1)
        return sum(w * np.sin(2 * np.pi * f * m * k) for m, w in ((1, 1), (2, 0.5), (3, 0.3), (4, 0.15))) * e
    horn = lp(note(196, 0.0, 1.0) + note(261.6, 1.05, 2.3), 1600) * 0.4 + bp(noise(2.4), 300, 1200) * 0.03
    write("horn", norm(horn, 0.7))


def main():
    import sys
    os.makedirs(OUT, exist_ok=True)
    if "--endgame" in sys.argv:
        endgame_sfx(); return
    for name, fn in (("wind", wind), ("wind_cold", lambda: wind(True)), ("rain", rain), ("fire", fire), ("crypt", crypt),
                     ("forest", forest), ("camp", camp), ("night", night_garden), ("town", town), ("hall", hall),
                     ("heartbeat", heartbeat)):
        write("amb_" + name, fn(), ogg=True)
    sfx()
    endgame_sfx()


if __name__ == "__main__":
    main()
