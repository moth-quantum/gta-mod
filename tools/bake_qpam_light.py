#!/usr/bin/env python3
"""Bake the force field's QPAM light: 8 frames, 256 x 256 greyscale PNGs in recorded/forcefield_light/.

Run once on the Mac (needs qiskit and qiskit-aer); the game embeds the PNGs and never runs QPAM itself.

    python3 tools/bake_qpam_light.py

Source: a soft caustic pattern (light through rippling water) that tiles seamlessly and drifts one step
per frame. QPAM on Qiskit Aer, real circuits: each 16 x 16 block (256 values) is encoded as the
amplitudes of an 8-qubit state (square roots of the normalised values), every qubit gets RX(0.3 pi),
and the state is measured 2000 times. The counts are decoded back to values (count share x the block's
total). RX mixes each pixel with the pixels whose index differs in one bit, so light leaks into
mirrored positions inside the block; the shots add grain. Simulation only, nothing is sent anywhere.

Each PNG holds the light L (0 black to 1 white). The game reads them into ForceField.LightField and
samples one value per triangle: colour x (0.8 + 1.3 L) + 0.5 L^2 at opacity x 1.2 x (0.35 + 0.9 L),
as in the 11 Nov preview (GTA single player can't make a texture from a file, so it isn't drawn as one).
"""
import math
from pathlib import Path

import numpy as np
from PIL import Image
from qiskit import QuantumCircuit, transpile
from qiskit_aer import AerSimulator

OUT = Path(__file__).resolve().parent.parent / "recorded" / "forcefield_light"
SIZE, BLOCK, FRAMES = 256, 16, 8
THETA = 0.3 * math.pi
SHOTS = 2000
SEED = 1000


def caustic(phase):
    y, x = np.mgrid[0:SIZE, 0:SIZE] / SIZE * 2 * math.pi
    v = np.zeros((SIZE, SIZE))
    for k, (a, b, s) in enumerate([(3, 1, 1.0), (-2, 3, 0.8), (1, -4, 0.7), (4, 2, 0.5)]):   # integer waves: tiles seamlessly
        v += s * np.cos(a * x + b * y + phase * (1 + 0.3 * k) + k)
    v = 1 - np.abs(np.tanh(v * 0.9))          # bright where the waves cancel: thin light lines
    return v ** 3 * 255


def qpam(src, sim, seed):
    n = int(math.log2(BLOCK * BLOCK))
    circuits, totals = [], []
    for by in range(0, SIZE, BLOCK):
        for bx in range(0, SIZE, BLOCK):
            vals = src[by:by + BLOCK, bx:bx + BLOCK].ravel() + 1.0   # +1 so a dark block is still a valid state
            qc = QuantumCircuit(n)
            qc.initialize(np.sqrt(vals / vals.sum()).tolist(), range(n))
            for q in range(n):
                qc.rx(THETA, q)
            qc.measure_all()
            circuits.append(qc)
            totals.append(vals.sum())
    result = sim.run(transpile(circuits, sim), shots=SHOTS, seed_simulator=seed).result()
    img = np.zeros((SIZE, SIZE))
    i = 0
    for by in range(0, SIZE, BLOCK):
        for bx in range(0, SIZE, BLOCK):
            block = np.zeros(BLOCK * BLOCK)
            for bits, c in result.get_counts(i).items():
                block[int(bits.replace(" ", ""), 2)] = c
            img[by:by + BLOCK, bx:bx + BLOCK] = (block / SHOTS * totals[i]).reshape(BLOCK, BLOCK)
            i += 1
    return np.clip(img / np.percentile(img, 99.5), 0, 1)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    sim = AerSimulator()
    for f in range(FRAMES):
        light = qpam(caustic(2 * math.pi * f / FRAMES), sim, SEED + f)
        Image.fromarray((light * 255).round().astype(np.uint8), "L").save(OUT / f"light_{f}.png", optimize=True)
        print(f"frame {f}: mean light {light.mean():.3f}")
    print(f"wrote {FRAMES} frames to {OUT}")


if __name__ == "__main__":
    main()
